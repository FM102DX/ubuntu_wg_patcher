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
                var fullScript = "#!/usr/bin/env bash\n" + preflight;
                Log.Debug("---- BEGIN preflight.sh ----\n{Script}\n---- END preflight.sh ----", fullScript);
                await _ssh.UploadTextAsync(tmpScript, fullScript, ct);
                var (exitP, stdoutP, stderrP) = await _ssh.RunCommandAsync($"bash {tmpScript}", TimeSpan.FromMinutes(10), ct);
                if (exitP != 0)
                {
                    throw new Exception($"preflight failed: {stderrP}\n{stdoutP}");
                }

                // Print diagnostics output captured from the preflight script
                LogLine("Diagnostics output:");
                foreach (var l in (stdoutP ?? string.Empty).Split('\n'))
                {
                    var line = l.TrimEnd();
                    if (!string.IsNullOrWhiteSpace(line)) LogLine(line);
                }
                foreach (var l in (stderrP ?? string.Empty).Split('\n'))
                {
                    var line = l.TrimEnd();
                    if (!string.IsNullOrWhiteSpace(line)) LogLine(line);
                }
                LogLine("Diagnostics complete.");

                // Ensure Docker is installed (one attempt)
                LogLine("Checking Docker...");
                var (chkDocker, _, _) = await _ssh.RunCommandAsync("command -v docker >/dev/null 2>&1", TimeSpan.FromSeconds(20), ct);
                if (chkDocker != 0)
                {
                    LogLine("Docker not found. Installing Docker (one attempt)...");
                    await _ssh.RunCommandAsync("export DEBIAN_FRONTEND=noninteractive; apt-get update -y", TimeSpan.FromMinutes(5), ct);
                    await _ssh.RunCommandAsync("apt-get install -y curl ca-certificates", TimeSpan.FromMinutes(5), ct);
                    var (exitInstall, outInstall, errInstall) = await _ssh.RunCommandAsync("curl -fsSL https://get.docker.com | sh", TimeSpan.FromMinutes(10), ct);
                    if (exitInstall != 0)
                    {
                        // Fallback to distro package (useful for EOL systems where convenience script refuses)
                        LogLine("WARN: Docker convenience script failed, trying apt-get install docker.io as fallback...");
                        var (exitAptDocker, outAptDocker, errAptDocker) = await _ssh.RunCommandAsync("apt-get install -y docker.io", TimeSpan.FromMinutes(10), ct);
                        if (exitAptDocker != 0)
                        {
                            throw new Exception($"Docker installation failed:\nscript: {errInstall}\n{outInstall}\naptdocker: {errAptDocker}\n{outAptDocker}");
                        }
                    }
                    await _ssh.RunCommandAsync("systemctl enable --now docker 2>/dev/null || service docker start 2>/dev/null || true", TimeSpan.FromMinutes(2), ct);
                    var (chkDocker2, stdoutDv, stderrDv) = await _ssh.RunCommandAsync("docker --version", TimeSpan.FromSeconds(30), ct);
                    if (chkDocker2 != 0)
                    {
                        throw new Exception($"Docker installation unsuccessful: {stderrDv}\n{stdoutDv}");
                    }
                    else
                    {
                        LogLine($"Docker installed: {stdoutDv.Trim()}");
                    }
                }

                // Ensure Docker Compose is installed (one attempt), after Docker is present
                LogLine("Checking Docker Compose...");
                var (chkCompose, _, _) = await _ssh.RunCommandAsync("docker compose version >/dev/null 2>&1", TimeSpan.FromSeconds(20), ct);
                var composeOk = chkCompose == 0;
                if (!composeOk)
                {
                    var (chkComposeV1, _, _) = await _ssh.RunCommandAsync("command -v docker-compose >/dev/null 2>&1", TimeSpan.FromSeconds(20), ct);
                    composeOk = chkComposeV1 == 0;
                }
                if (!composeOk)
                {
                    LogLine("Docker Compose not found. Installing Docker Compose (one attempt)...");
                    await _ssh.RunCommandAsync("export DEBIAN_FRONTEND=noninteractive; apt-get update -y", TimeSpan.FromMinutes(5), ct);
                    var (exitPlug, outPlug, errPlug) = await _ssh.RunCommandAsync("apt-get install -y docker-compose-plugin", TimeSpan.FromMinutes(10), ct);
                    var (verPlugin, _, _) = await _ssh.RunCommandAsync("docker compose version >/dev/null 2>&1", TimeSpan.FromSeconds(20), ct);
                    if (verPlugin != 0)
                    {
                        // Fallback to legacy package
                        var (exitV1, outV1, errV1) = await _ssh.RunCommandAsync("apt-get install -y docker-compose", TimeSpan.FromMinutes(10), ct);
                        var (verV1, stdoutV1, stderrV1) = await _ssh.RunCommandAsync("docker-compose --version", TimeSpan.FromSeconds(30), ct);
                        if (verV1 != 0)
                        {
                            // Final fallback: install static docker-compose v1 binary
                            LogLine("WARN: apt docker-compose not available. Installing docker-compose v1 binary...");
                            var installBin = string.Join(" ", new[]
                            {
                                "ARCH=$(uname -m)",
                                "if [ \"$ARCH\" = \"x86_64\" ] || [ \"$ARCH\" = \"amd64\" ]; then SUF=x86_64;",
                                "elif [ \"$ARCH\" = \"aarch64\" ] || [ \"$ARCH\" = \"arm64\" ]; then SUF=aarch64;",
                                "elif [ \"$ARCH\" = \"armv7l\" ]; then SUF=armv7l; else SUF=$ARCH; fi;",
                                "curl -L \"https://github.com/docker/compose/releases/download/1.29.2/docker-compose-Linux-$SUF\" -o /usr/local/bin/docker-compose",
                                "chmod +x /usr/local/bin/docker-compose",
                                "ln -sf /usr/local/bin/docker-compose /usr/bin/docker-compose || true"
                            });
                            var (exitBin, outBin, errBin) = await _ssh.RunCommandAsync(installBin, TimeSpan.FromMinutes(5), ct);
                            var (verV1b, stdoutV1b, stderrV1b) = await _ssh.RunCommandAsync("docker-compose --version", TimeSpan.FromSeconds(30), ct);
                            if (verV1b != 0)
                            {
                                throw new Exception($"Docker Compose installation unsuccessful:\nplugin: {errPlug}\n{outPlug}\nlegacy: {errV1}\n{outV1}\nlegacy-verify: {stderrV1}\n{stdoutV1}\nbinary: {errBin}\n{outBin}\nbinary-verify: {stderrV1b}\n{stdoutV1b}");
                            }
                            else
                            {
                                LogLine($"Compose installed: {stdoutV1b.Trim()}");
                            }
                        }
                        else
                        {
                            LogLine($"Compose installed: {stdoutV1.Trim()}");
                        }
                    }
                    else
                    {
                        var (_, stdoutC, stderrC) = await _ssh.RunCommandAsync("docker compose version", TimeSpan.FromSeconds(30), ct);
                        LogLine($"Compose installed: {stdoutC.Trim()} {stderrC.Trim()}");
                    }
                }
                // --- Remove existing WireGuard (container and directory) ---
                LogLine("Removing existing WireGuard installation (container and directory)...");
                var checkContainerCmd = "docker ps -aq -f name=^wireguard$";
                var (_, outChk, errChk) = await _ssh.RunCommandAsync(checkContainerCmd, TimeSpan.FromSeconds(20), ct);
                if (!string.IsNullOrWhiteSpace((outChk ?? string.Empty).Trim()))
                {
                    LogLine("Existing container 'wireguard' found. Removing...");
                    await _ssh.RunCommandAsync("docker rm -f wireguard 2>/dev/null || true", TimeSpan.FromMinutes(2), ct);
                }
                var (_, outVerify, errVerify) = await _ssh.RunCommandAsync(checkContainerCmd, TimeSpan.FromSeconds(20), ct);
                if (!string.IsNullOrWhiteSpace((outVerify ?? string.Empty).Trim()))
                {
                    throw new Exception($"Failed to remove WireGuard container: {errVerify}\n{outVerify}");
                }

                // Remove directory /opt/wireguard
                await _ssh.RunCommandAsync("rm -rf /opt/wireguard 2>/dev/null || true", TimeSpan.FromMinutes(2), ct);
                var (exitDirOk, _, errDir) = await _ssh.RunCommandAsync("test ! -d /opt/wireguard", TimeSpan.FromSeconds(10), ct);
                if (exitDirOk != 0)
                {
                    throw new Exception($"Failed to remove /opt/wireguard directory: {errDir}");
                }

                // --- WireGuard section ---
                LogLine("Preparing /opt/wireguard and docker-compose.yml...");
                await _ssh.EnsureDirectoryAsync("/opt/wireguard", ct);

                var publicIp = await _ssh.GetPublicIpAsync(ct);
                var compose = ComposeTemplate.Generate(publicIp, session.WgPort, session.Peers);
                var composePath = "/opt/wireguard/docker-compose.yml";
                await _ssh.UploadTextAsync(composePath, compose, ct);

                LogLine("Starting WireGuard container...");
                var composeSelect = "COMPOSE=\"docker compose\"; docker compose version >/dev/null 2>&1 || COMPOSE=\"docker-compose\";";
                var pullCmd = composeSelect + " $COMPOSE -f /opt/wireguard/docker-compose.yml pull";
                await _ssh.RunCommandAsync(pullCmd, TimeSpan.FromMinutes(15), ct);
                var startCmd = composeSelect + " $COMPOSE -f /opt/wireguard/docker-compose.yml up -d";
                var (exitUp, stdoutUp, stderrUp) = await _ssh.RunCommandAsync(startCmd, TimeSpan.FromMinutes(10), ct);
                if (exitUp != 0)
                {
                    throw new Exception($"docker compose up failed: {stderrUp}\n{stdoutUp}");
                }

                // Verify container is running (with small backoff retries)
                LogLine("Verifying container is running...");
                var inspectCmd = "docker inspect -f '{{.State.Running}}' wireguard 2>/dev/null | tr -d '\r'";
                int exitInspect = 0; string outInspect = string.Empty; string errInspect = string.Empty;
                var runningOk = false;
                for (int attempt = 0; attempt < 3 && !runningOk; attempt++)
                {
                    if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    (exitInspect, outInspect, errInspect) = await _ssh.RunCommandAsync(inspectCmd, TimeSpan.FromSeconds(120), ct);
                    runningOk = exitInspect == 0 && string.Equals((outInspect ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase);
                }
                if (!runningOk)
                {
                    // Show docker ps for context then fail
                    var (_, outPsErr, errPsErr) = await _ssh.RunCommandAsync("docker ps -a --filter name=^wireguard$", TimeSpan.FromSeconds(60), ct);
                    throw new Exception($"WireGuard container is not running: {errInspect}\ninspect: {outInspect}\nps: {errPsErr}\n{outPsErr}");
                }

                LogLine("docker ps output:");
                var (exitPs, stdoutPs, stderrPs) = await _ssh.RunCommandAsync("docker ps", TimeSpan.FromSeconds(60), ct);
                foreach (var l in (stdoutPs ?? string.Empty).Split('\n'))
                {
                    var line = l.TrimEnd();
                    if (!string.IsNullOrWhiteSpace(line)) LogLine(line);
                }
                foreach (var l in (stderrPs ?? string.Empty).Split('\n'))
                {
                    var line = l.TrimEnd();
                    if (!string.IsNullOrWhiteSpace(line)) LogLine(line);
                }

                LogLine("SUCCESS: Finished");

                return new RunnerResult
                {
                    PublicIp = publicIp,
                    GeoJson = string.Empty,
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
