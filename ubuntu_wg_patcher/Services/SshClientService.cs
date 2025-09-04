using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public event Action<string>? CommandExecuting;

        public async Task ConnectAsync(string host, int port, string username, string password, CancellationToken ct)
        {
            await Task.Run(() =>
            {
                // Connection/handshake timeout: 60s (recommended 15–60s)
                var connInfo = new PasswordConnectionInfo(host, port, username, password)
                {
                    Timeout = TimeSpan.FromSeconds(60)
                };

                // Create SSH with robust timeouts and keep-alives
                _ssh = new SshClient(connInfo)
                {
                    // KeepAlive to prevent NAT/firewall idle disconnects: 15 seconds (recommended 10–30s)
                    KeepAliveInterval = TimeSpan.FromSeconds(15)
                };
                _ssh.ConnectionInfo.RetryAttempts = 3;
                _ssh.Connect();

                // SFTP with similar settings
                _sftp = new SftpClient(connInfo)
                {
                    OperationTimeout = TimeSpan.FromMinutes(10),
                    KeepAliveInterval = TimeSpan.FromSeconds(15)
                };
                _sftp.Connect();
            }, ct);
        }

        public async Task<(int ExitCode, string Stdout, string Stderr)> RunCommandAsync(string command, TimeSpan? timeout, CancellationToken ct)
        {
            if (_ssh == null) throw new InvalidOperationException("SSH client not connected");
            var attempts = 0;
            while (true)
            {
                try
                {
                    return await Task.Run(() =>
                    {
                        CommandExecuting?.Invoke(command);
                        using var cmd = _ssh.CreateCommand(command);
                        // Command timeout: use provided value, else fall back to generous default 6 minutes
                        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(6);
                        var asyncResult = cmd.BeginExecute();
                        while (!asyncResult.IsCompleted)
                        {
                            ct.ThrowIfCancellationRequested();
                            Thread.Sleep(50);
                        }
                        // Ensure command is finalized and output streams are flushed
                        cmd.EndExecute(asyncResult);
                        var stdout = cmd.Result;
                        var stderr = cmd.Error;
                        var exit = cmd.ExitStatus;
                        return (exit, stdout, stderr);
                    }, ct);
                }
                catch (SshOperationTimeoutException) when (attempts++ == 0)
                {
                    // One quick retry: reconnect if needed, then retry once
                    try { if (_ssh != null && !_ssh.IsConnected) _ssh.Connect(); } catch { }
                    await Task.Delay(250, ct);
                    continue;
                }
            }
        }

        public async Task UploadTextAsync(string remotePath, string content, CancellationToken ct)
        {
            if (_sftp == null) throw new InvalidOperationException("SFTP client not connected");
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

        public async Task EnsureDirectoryAsync(string remoteDir, CancellationToken ct)
        {
            if (_sftp == null) throw new InvalidOperationException("SFTP client not connected");
            await Task.Run(() => EnsureAllDirectories(remoteDir.Replace("\\", "/")), ct);
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
            if (_sftp == null) throw new InvalidOperationException("SFTP client not connected");
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
        }
    }
}
