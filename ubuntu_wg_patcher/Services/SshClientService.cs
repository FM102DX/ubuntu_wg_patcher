using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ubuntu_wg_patcher.Services
{
    public interface ISshClientService : IDisposable
    {
        event Action<string>? CommandExecuting;
        Task ConnectAsync(string host, int port, string username, string password, CancellationToken ct);
        Task<(int ExitCode, string Stdout, string Stderr)> RunCommandAsync(string command, TimeSpan? timeout, CancellationToken ct);
        Task UploadTextAsync(string remotePath, string content, CancellationToken ct);
        Task EnsureDirectoryAsync(string remoteDir, CancellationToken ct);
        Task<List<string>> DownloadPeerConfigsAsync(string remoteConfigRoot, string localExportDir, CancellationToken ct);
        Task<string> GetPublicIpAsync(CancellationToken ct);
        Task<string> GetGeoJsonAsync(CancellationToken ct);
    }

    public class SshClientService : ISshClientService
    {
        private SshClient? _ssh;
        private SftpClient? _sftp;
        private PasswordConnectionInfo? _connInfo;
        private readonly object _connLock = new();
        private int _activeCommands = 0;
        private readonly List<(SshClient? ssh, SftpClient? sftp)> _zombies = new();

        public event Action<string>? CommandExecuting;
        // Default command-level timeout used when a specific timeout is not provided
        public TimeSpan DefaultCommandTimeout { get; set; } = TimeSpan.FromMinutes(10);

        public async Task ConnectAsync(string host, int port, string username, string password, CancellationToken ct)
        {
            await Task.Run(() =>
            {
                // Connection/handshake timeout: 30s (shorter to allow more retries during network disruption)
                _connInfo = new PasswordConnectionInfo(host, port, username, password)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                // Always create fresh clients on initial Connect
                ConnectOrReconnect(forceNew: true);
            }, ct);
        }

        private static bool IsConnectedSafe(SshClient? client)
        {
            if (client == null) return false;
            try { return client.IsConnected; }
            catch (ObjectDisposedException) { return false; }
            catch { return false; }
        }

        private static bool IsConnectedSafe(SftpClient? client)
        {
            if (client == null) return false;
            try { return client.IsConnected; }
            catch (ObjectDisposedException) { return false; }
            catch { return false; }
        }

        private void ConnectOrReconnect(bool forceNew = false)
        {
            if (_connInfo == null)
                throw new InvalidOperationException("Connection info not initialized");

            lock (_connLock)
            {
                if (!forceNew && IsConnectedSafe(_ssh))
                {
                    // Already connected
                    return;
                }

                Exception? lastEx = null;
                // More attempts with shorter timeout to ride out temporary network changes
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    SshClient? newSsh = null; SftpClient? newSftp = null;
                    try
                    {
                        // Create new clients with robust timeouts and keep-alives
                        newSsh = new SshClient(_connInfo)
                        {
                            KeepAliveInterval = TimeSpan.FromSeconds(15)
                        };
                        newSsh.ConnectionInfo.RetryAttempts = 3;
                        newSsh.Connect();

                        newSftp = new SftpClient(_connInfo)
                        {
                            OperationTimeout = TimeSpan.FromMinutes(10),
                            KeepAliveInterval = TimeSpan.FromSeconds(15)
                        };
                        newSftp.Connect();

                        // Swap-in new clients; defer disposing previous if an operation is active
                        var oldSsh = _ssh; var oldSftp = _sftp;
                        _ssh = newSsh; _sftp = newSftp;
                        newSsh = null; newSftp = null; // ownership transferred

                        if (oldSsh != null || oldSftp != null)
                        {
                            if (System.Threading.Interlocked.CompareExchange(ref _activeCommands, 0, 0) > 0)
                            {
                                _zombies.Add((oldSsh, oldSftp));
                            }
                            else
                            {
                                try { oldSsh?.Dispose(); } catch { }
                                try { oldSftp?.Dispose(); } catch { }
                            }
                        }

                        // success
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        try { newSsh?.Dispose(); } catch { }
                        try { newSftp?.Dispose(); } catch { }
                        if (attempt < 2)
                        {
                            Thread.Sleep(500 + attempt * 500);
                            continue;
                        }
                        throw;
                    }
                }
                if (lastEx != null) throw lastEx;
            }
        }

        private void DisposeZombiesIfIdle()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _activeCommands, 0, 0) == 0)
            {
                lock (_connLock)
                {
                    foreach (var (ssh, sftp) in _zombies)
                    {
                        try { ssh?.Dispose(); } catch { }
                        try { sftp?.Dispose(); } catch { }
                    }
                    _zombies.Clear();
                }
            }
        }

        public async Task<(int ExitCode, string Stdout, string Stderr)> RunCommandAsync(string command, TimeSpan? timeout, CancellationToken ct)
        {
            var attempts = 0;
            const int maxAttempts = 3; // initial try + up to 2 retries
            while (true)
            {
                // Pin a client instance for this attempt under the connection lock
                SshClient? client;
                lock (_connLock)
                {
                    if (!IsConnectedSafe(_ssh))
                    {
                        ConnectOrReconnect();
                    }
                    // Mark an active command before exposing the pinned client,
                    // so ConnectOrReconnect will not dispose it while we prepare.
                    System.Threading.Interlocked.Increment(ref _activeCommands);
                    client = _ssh;
                }
                try
                {
                    return await Task.Run(() =>
                    {
                        CommandExecuting?.Invoke(command);
                        if (client == null) throw new InvalidOperationException("SSH client not connected");
                        // Create the command under lock to avoid concurrent dispose during CreateCommand
                        Renci.SshNet.SshCommand cmd;
                        lock (_connLock)
                        {
                            cmd = client.CreateCommand(command);
                        }
                        using (cmd)
                        {
                            // Start async execution
                            var asyncResult = cmd.BeginExecute();
                            // Our own deadline-based wait (do NOT use cmd.CommandTimeout)
                            DateTime? deadline = timeout.HasValue ? DateTime.UtcNow + timeout.Value : (DateTime?)null;
                            while (!asyncResult.IsCompleted)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (deadline.HasValue && DateTime.UtcNow >= deadline.Value)
                                {
                                    throw new SshOperationTimeoutException($"Command timed out after {timeout.Value}.");
                                }
                                Thread.Sleep(50);
                            }
                            // Ensure command is finalized and output streams are flushed
                            cmd.EndExecute(asyncResult);
                            var stdout = cmd.Result;
                            var stderr = cmd.Error;
                            var exit = cmd.ExitStatus;
                            return (exit, stdout, stderr);
                        }
                    }, ct);
                }
                catch (ObjectDisposedException) when (attempts++ < maxAttempts - 1)
                {
                    // Client was disposed concurrently; force a fresh reconnect and retry
                    try { ConnectOrReconnect(forceNew: true); } catch { }
                    await Task.Delay(400, ct);
                    continue;
                }
                catch (InvalidOperationException ex) when (attempts++ < maxAttempts - 1 &&
                                                          (ex.Message?.IndexOf("not connected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                           ex.Message?.IndexOf("disposed", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    // Treat as transient connectivity; reconnect and retry
                    try { ConnectOrReconnect(forceNew: true); } catch { }
                    await Task.Delay(400, ct);
                    continue;
                }
                catch (SshOperationTimeoutException) when (attempts++ < maxAttempts - 1)
                {
                    // Retry: reconnect if needed, then retry
                    try { ConnectOrReconnect(); } catch { }
                    await Task.Delay(300, ct);
                    continue;
                }
                catch (SshConnectionException) when (attempts++ < maxAttempts - 1)
                {
                    // Server aborted the connection; force a fresh reconnect and retry
                    try { ConnectOrReconnect(forceNew: true); } catch { }
                    await Task.Delay(500, ct);
                    continue;
                }
                catch (SocketException) when (attempts++ < maxAttempts - 1)
                {
                    // Network error; force reconnect and retry
                    try { ConnectOrReconnect(forceNew: true); } catch { }
                    await Task.Delay(500, ct);
                    continue;
                }
                finally
                {
                    System.Threading.Interlocked.Decrement(ref _activeCommands);
                    DisposeZombiesIfIdle();
                }
            }
        }

        public async Task UploadTextAsync(string remotePath, string content, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref _activeCommands);
            try
            {
                if (!IsConnectedSafe(_sftp))
                {
                    ConnectOrReconnect();
                    if (!IsConnectedSafe(_sftp)) throw new InvalidOperationException("SFTP client not connected");
                }
                // Normalize to LF to prevent bash errors like: set: -\r: invalid option
                var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(normalized));
                await Task.Run(() =>
                {
                    var dir = Path.GetDirectoryName(remotePath)!.Replace("\\", "/");
                    EnsureAllDirectories(dir);
                    _sftp!.UploadFile(ms, remotePath, true);
                }, ct);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _activeCommands);
                DisposeZombiesIfIdle();
            }
        }

        public async Task EnsureDirectoryAsync(string remoteDir, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref _activeCommands);
            try
            {
                if (!IsConnectedSafe(_sftp))
                {
                    ConnectOrReconnect();
                    if (!IsConnectedSafe(_sftp)) throw new InvalidOperationException("SFTP client not connected");
                }
                await Task.Run(() => EnsureAllDirectories(remoteDir.Replace("\\", "/")), ct);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _activeCommands);
                DisposeZombiesIfIdle();
            }
        }

        private void EnsureAllDirectories(string remoteDir)
        {
            if (_sftp == null) throw new InvalidOperationException("SFTP client not connected");
            var parts = remoteDir.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
            var path = "/";
            foreach (var p in parts)
            {
                path = path.EndsWith("/") ? path + p : path + "/" + p;
                if (!_sftp.Exists(path))
                {
                    _sftp.CreateDirectory(path);
                }
            }
        }

        public async Task<List<string>> DownloadPeerConfigsAsync(string remoteConfigRoot, string localExportDir, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref _activeCommands);
            try
            {
                if (!IsConnectedSafe(_sftp))
                {
                    ConnectOrReconnect();
                    if (!IsConnectedSafe(_sftp)) throw new InvalidOperationException("SFTP client not connected");
                }
                return await Task.Run(() =>
                {
                    Directory.CreateDirectory(localExportDir);
                    var result = new List<string>();
                    var root = remoteConfigRoot.Replace("\\", "/");
                    var entries = _sftp.ListDirectory(root);
                    foreach (var e in entries)
                    {
                        if (e.IsDirectory && e.Name.StartsWith("peer", StringComparison.OrdinalIgnoreCase))
                        {
                            var peerDir = e.FullName;
                            var files = _sftp.ListDirectory(peerDir).Where(f => !f.IsDirectory && f.Name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase));
                            foreach (var f in files)
                            {
                                var localPath = Path.Combine(localExportDir, f.Name);
                                using var fs = File.Create(localPath);
                                _sftp.DownloadFile(f.FullName, fs);
                                result.Add(localPath);
                            }
                        }
                    }
                    return result;
                }, ct);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _activeCommands);
                DisposeZombiesIfIdle();
            }
        }

        public async Task<string> GetPublicIpAsync(CancellationToken ct)
        {
            var (code, stdout, _) = await RunCommandAsync("curl -s --max-time 60 https://api.ipify.org", TimeSpan.FromSeconds(60), ct);
            if (code != 0) return string.Empty;
            return stdout.Trim();
        }

        public async Task<string> GetGeoJsonAsync(CancellationToken ct)
        {
            var (code, stdout, _) = await RunCommandAsync("curl -s --max-time 60 https://ipinfo.io/json", TimeSpan.FromSeconds(60), ct);
            if (code != 0) return string.Empty;
            return stdout.Trim();
        }

        public void Dispose()
        {
            try { _ssh?.Dispose(); } catch { }
            try { _sftp?.Dispose(); } catch { }
            DisposeZombiesIfIdle();
        }
    }
}
