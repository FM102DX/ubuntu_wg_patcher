using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Services
{
    public class DiagnosticsService : IDiagnosticsService
    {
        private readonly ISshClientService _ssh;
        private readonly IServerInfoReportService _servrinfo;
        public DiagnosticsService(ISshClientService ssh, IServerInfoReportService servrinfo)
        {
            _ssh = ssh;
            _servrinfo = servrinfo;
        }

        public async Task<string?> RunServerInfoAsync(SessionParams cfg, IProgress<string> progress, CancellationToken ct)
        {
            void Log(string m) => progress.Report(m);

            if (string.IsNullOrWhiteSpace(cfg.ExportPath))
            {
                Log("Error: Export folder is empty");
                return null;
            }

            string localScriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "wg_diag.sh");
            if (!File.Exists(localScriptPath))
            {
                Log($"Fatal: Local script not found: {localScriptPath}");
                return null;
            }

            try
            {
                Log($"ServerInfo: connecting to {cfg.Host}:{cfg.Port} as {cfg.Login}...");
                await _ssh.ConnectAsync(cfg.Host, cfg.Port, cfg.Login, cfg.Password, ct);
            }
            catch (Exception ex)
            {
                Log($"Fatal: SSH connect failed: {ex.Message}");
                return null;
            }

            var jobId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var remoteJobDir = $"/tmp/wg_job_{jobId}";
            var remoteScript = $"{remoteJobDir}/wg_diag.sh";

            try
            {
                Log("ServerInfo: preparing remote temp folder...");
                await _ssh.RunCommandAsync($"mkdir -p {remoteJobDir}", TimeSpan.FromSeconds(20), ct);
            }
            catch (Exception ex)
            {
                Log($"Fatal: Can't prepare remote folder: {ex.Message}");
                return null;
            }

            try
            {
                Log("ServerInfo: uploading script...");
                var text = await File.ReadAllTextAsync(localScriptPath, ct);
                await _ssh.UploadTextAsync(remoteScript, text, ct);
            }
            catch (Exception ex)
            {
                Log($"Fatal: Upload failed: {ex.Message}");
                return null;
            }

            int exit;
            string? stdout;
            string? stderr;
            try
            {
                Log("ServerInfo: running script (may take a while)...");
                (exit, stdout, stderr) = await _ssh.RunCommandAsync($"bash {remoteScript}", TimeSpan.FromMinutes(6), ct);
            }
            catch (Exception ex)
            {
                Log($"Fatal: Remote execution failed: {ex.Message}");
                return null;
            }

            if (exit != 0)
            {
                Log($"Error: Diagnostic script exit code {exit}. {stderr}");
            }

            // Parse locations from output
            string all = ($"{stdout}\n{stderr}") ?? string.Empty;
            string? remoteLogPath = null;
            string? remoteOutDir = null;
            foreach (var line in all.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("Report saved to:"))
                {
                    remoteLogPath = l.Substring("Report saved to:".Length).Trim();
                }
                else if (l.StartsWith("Extra files (configs/raw dumps) in:"))
                {
                    remoteOutDir = l.Substring("Extra files (configs/raw dumps) in:".Length).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(remoteOutDir))
            {
                var (_, latest, _) = await _ssh.RunCommandAsync("ls -1d /tmp/wg_diag_* 2>/dev/null | tail -n 1", TimeSpan.FromSeconds(20), ct);
                remoteOutDir = (latest ?? string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(remoteOutDir))
            {
                Log("Fatal: Can't locate remote diagnostic folder.");
                return null;
            }

            try
            {
                Directory.CreateDirectory(cfg.ExportPath);
            }
            catch (Exception ex)
            {
                Log($"Fatal: Can't prepare local export folder: {ex.Message}");
                return null;
            }

            // Determine remote report file
            var remoteReport = !string.IsNullOrWhiteSpace(remoteLogPath)
                ? remoteLogPath!
                : (remoteOutDir.TrimEnd('/', '\\') + "/report.txt");

            // Compose local single-file target name
            string cfgPart = SanitizeFilePart(cfg.ConfigName);
            string localFile = Path.Combine(cfg.ExportPath, $"diag_report_{cfgPart}_{Guid.NewGuid():N}.txt");

            try
            {
                await _ssh.DownloadFileAsync(remoteReport, localFile, ct);
            }
            catch (Exception ex)
            {
                Log($"Fatal: Download failed: {ex.Message}");
                return null;
            }

            // Append Servrinfo: WireGuard (keys & peers) section at the end of the downloaded report
            try
            {
                var block = await _servrinfo.CollectWireGuardServrinfoAsync(cfg, progress, ct);
                await File.AppendAllTextAsync(localFile, "\n\n" + block + "\n", ct);
                Log("SuccessMsg: Servrinfo section appended to report");
            }
            catch (Exception ex)
            {
                Log($"WARN: failed to append Servrinfo section: {ex.Message}");
            }

            Log($"SuccessMsg: ServerInfo report downloaded to: {localFile}");
            return localFile;
        }

        private static string SanitizeFilePart(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "config";
            var s = input;
            foreach (var ch in Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');
            return s;
        }
    }
}
