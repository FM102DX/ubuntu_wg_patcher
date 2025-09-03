using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using ubuntu_wg_patcher.Logging;
using ubuntu_wg_patcher.Models;
using ubuntu_wg_patcher.Templates;

namespace ubuntu_wg_patcher.Services
{
    public class Runner
    {
        private readonly ISshClientService _ssh;

        public Runner(ISshClientService ssh)
        {
            _ssh = ssh;
        }

        public async Task<RunnerResult> RunAsync(SessionParams session, IProgress<string> progress, CancellationToken ct)
        {
            void LogLine(string line)
            {
                Log.Information(line);
                progress.Report(line);
            }

            // Subscribe to SSH command logging so each executed command is reported once
            void onCmd(string cmd) => progress.Report($"CMD: {cmd}");
            _ssh.CommandExecuting += onCmd;
            try
            {
                LogLine($"Connecting to {session.Host}:{session.Port} as {session.Login}...");
                await _ssh.ConnectAsync(session.Host, session.Port, session.Login, session.Password, ct);

                LogLine("Preflight checks and host configuration...");
                var preflight = RemoteScripts.BuildPreflightScript(session.WgPort, session.DisableIPv6);
                var tmpScript = "/tmp/wg_preflight.sh";
                await _ssh.UploadTextAsync(tmpScript, "#!/usr/bin/env bash\n" + preflight, ct);
                var (exitP, stdoutP, stderrP) = await _ssh.RunCommandAsync($"bash {tmpScript}", TimeSpan.FromMinutes(10), ct);
                if (exitP != 0)
                {
                    throw new Exception($"preflight failed: {stderrP}\n{stdoutP}");
                }

                LogLine("Fetching public IP and GeoIP...");
                var publicIp = await _ssh.GetPublicIpAsync(ct);
                var geoJson = await _ssh.GetGeoJsonAsync(ct);

                LogLine("Generating docker-compose.yml...");
                var compose = ComposeTemplate.Generate(publicIp, session.WgPort, session.Peers);
                var remoteDir = "/opt/wireguard";
                var composePath = remoteDir + "/docker-compose.yml";
                await _ssh.UploadTextAsync(composePath, compose, ct);

                LogLine("Starting WireGuard container...");
                var (exitC, stdoutC, stderrC) = await _ssh.RunCommandAsync("COMPOSE_CMD=$(cat /opt/wireguard/.compose_cmd 2>/dev/null || echo 'docker compose'); $COMPOSE_CMD -f /opt/wireguard/docker-compose.yml up -d", TimeSpan.FromMinutes(5), ct);
                if (exitC != 0)
                {
                    throw new Exception($"docker compose up failed: {stderrC}\n{stdoutC}");
                }

                // TODO: Ensure peers count if container was already initialized previously.

                LogLine("Verifying wg status and port listening...");
                await _ssh.RunCommandAsync("wg show || true", TimeSpan.FromSeconds(30), ct);
                await _ssh.RunCommandAsync($"ss -lunup | grep {session.WgPort} || true", TimeSpan.FromSeconds(30), ct);

                LogLine("Downloading peer configurations...");
                var localFiles = await _ssh.DownloadPeerConfigsAsync("/opt/wireguard/config", session.ExportPath, ct);

                LogLine("Post-processing peer configs (MTU=1380, PersistentKeepalive=25)...");
                foreach (var f in localFiles)
                {
                    PostProcessPeerConfig(f);
                }

                LogLine($"Done. Exported {localFiles.Count} peer configs to {session.ExportPath}");

                return new RunnerResult
                {
                    PublicIp = publicIp,
                    GeoJson = geoJson,
                    ExportPath = session.ExportPath,
                    LogFilePath = LogService.CurrentLogFilePath ?? string.Empty
                };
            }
            finally
            {
                _ssh.CommandExecuting -= onCmd;
            }
        }

        private static void PostProcessPeerConfig(string filePath)
        {
            var lines = File.ReadAllLines(filePath).ToList();
            // Ensure MTU under [Interface]
            var ifaceIndex = lines.FindIndex(l => l.Trim().Equals("[Interface]", StringComparison.OrdinalIgnoreCase));
            if (ifaceIndex >= 0)
            {
                var insertAt = ifaceIndex + 1;
                if (!lines.Any(l => l.TrimStart().StartsWith("MTU", StringComparison.OrdinalIgnoreCase)))
                    lines.Insert(insertAt, "MTU = 1380");
            }
            // Ensure PersistentKeepalive under [Peer]
            var peerIndex = lines.FindIndex(l => l.Trim().Equals("[Peer]", StringComparison.OrdinalIgnoreCase));
            if (peerIndex >= 0)
            {
                // place near the end of peer section
                var endIdx = lines.FindIndex(peerIndex + 1, l => l.StartsWith("[", StringComparison.Ordinal));
                if (endIdx == -1) endIdx = lines.Count;
                if (!lines.Skip(peerIndex).Take(endIdx - peerIndex).Any(l => l.TrimStart().StartsWith("PersistentKeepalive", StringComparison.OrdinalIgnoreCase)))
                {
                    lines.Insert(endIdx, "PersistentKeepalive = 25");
                }
            }
            File.WriteAllLines(filePath, lines);
        }
    }
}
