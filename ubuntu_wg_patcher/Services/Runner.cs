using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Net.Sockets;
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
            void SuccessMsg(string line)
            {
                progress.Report($"SuccessMsg: {line}");
            }

            // Wait for SSH TCP port reachability without relying on the current SSH session
            async Task<bool> WaitForSshPortAsync(string host, int port, TimeSpan totalTimeout, CancellationToken token)
            {
                var deadline = DateTime.UtcNow + totalTimeout;
                var attempt = 0;
                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    try
                    {
                        using var tcp = new TcpClient();
                        var connectTask = tcp.ConnectAsync(host, port);
                        var finished = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(5), token)) == connectTask;
                        if (finished && tcp.Connected)
                        {
                            LogLine($"SSH port reachable (attempt {attempt})");
                            return true;
                        }
                        else
                        {
                            LogLine($"SSH port not reachable yet (attempt {attempt})");
                        }
                    }
                    catch (Exception)
                    {
                        LogLine($"SSH port connect exception (attempt {attempt})");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
                return false;
            }

            // Subscribe to SSH command logging so each executed command is reported once
            void onCmd(string cmd) => progress.Report($"CMD: {cmd}");
            _ssh.CommandExecuting += onCmd;
            try
            {
                LogLine($"Connecting to {session.Host}:{session.Port} as {session.Login}...");
                await _ssh.ConnectAsync(session.Host, session.Port, session.Login, session.Password, ct);

                LogLine("Preflight checks and host configuration...");
                var preflight = RemoteScripts.BuildPreflightScript(51820, session.DisableIPv6);
                var tmpScript = "/tmp/wg_preflight.sh";
                var fullScript = "#!/usr/bin/env bash\n" + preflight;
                Log.Debug("---- BEGIN preflight.sh ----\n{Script}\n---- END preflight.sh ----", fullScript);
                await _ssh.UploadTextAsync(tmpScript, fullScript, ct);
                var (exitP, stdoutP, stderrP) = await _ssh.RunCommandAsync($"bash {tmpScript}", TimeSpan.FromMinutes(10), ct);
                if (exitP != 0)
                {
                    throw new Exception($"preflight failed: {stderrP}\n{stdoutP}");
                }

                // Stage 0: Connected and got Ubuntu version (from preflight 'OS: ...')
                try
                {
                    var osLine = (stdoutP ?? string.Empty)
                        .Split('\n')
                        .Select(l => l.TrimEnd())
                        .FirstOrDefault(l => l.StartsWith("OS: ", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(osLine))
                        SuccessMsg($"Connected. {osLine}");
                    else
                        SuccessMsg("Connected to host");
                }
                catch { SuccessMsg("Connected to host"); }

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
                        SuccessMsg($"Docker OK: {stdoutDv.Trim()}"); // Stage 1
                    }
                }
                else
                {
                    var (_, outDv, _) = await _ssh.RunCommandAsync("docker --version", TimeSpan.FromSeconds(30), ct);
                    SuccessMsg($"Docker OK: {outDv?.Trim()}"); // Stage 1
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
                                SuccessMsg($"Docker Compose OK: {stdoutV1b.Trim()}"); // Stage 2
                            }
                        }
                        else
                        {
                            LogLine($"Compose installed: {stdoutV1.Trim()}");
                            SuccessMsg($"Docker Compose OK: {stdoutV1.Trim()}"); // Stage 2
                        }
                    }
                    else
                    {
                        var (_, stdoutC, stderrC) = await _ssh.RunCommandAsync("docker compose version", TimeSpan.FromSeconds(30), ct);
                        LogLine($"Compose installed: {stdoutC.Trim()} {stderrC.Trim()}");
                        SuccessMsg($"Docker Compose OK: {stdoutC.Trim()} {stderrC.Trim()}"); // Stage 2
                    }
                }
                else
                {
                    // Already present: report version and success state
                    var (vPlugin, outPlugin, _) = await _ssh.RunCommandAsync("docker compose version", TimeSpan.FromSeconds(30), ct);
                    if (vPlugin == 0)
                        SuccessMsg($"Docker Compose OK: {outPlugin.Trim()}"); // Stage 2
                    else
                    {
                        var (v1, outV1only, _) = await _ssh.RunCommandAsync("docker-compose --version", TimeSpan.FromSeconds(30), ct);
                        if (v1 == 0) SuccessMsg($"Docker Compose OK: {outV1only.Trim()}");
                        else SuccessMsg("Docker Compose OK");
                    }
                }
                // --- Remove existing WireGuard (container and directory) ---
                LogLine("Removing existing WireGuard installation (container and directory)...");
                var checkContainerCmd = "docker ps -aq -f name=^wireguard$";
                var (chkContainerCmdRes, outChk, errChk) = await _ssh.RunCommandAsync(checkContainerCmd, TimeSpan.FromSeconds(20), ct);
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
                // Stage 3: WireGuard successfully removed (no container, no directory)
                SuccessMsg("WireGuard removed");

                // Diagnostics snapshot before starting WireGuard (to see where it hangs if it does)
                LogLine("Diagnostics: checking Docker and firewall state before start...");
                try
                {
                    // 1) Measure responsiveness of Docker daemon: docker version
                    var cmdTimeDockerVersion = "START=$(date +%s); docker version; EC=$?; END=$(date +%s); echo __ELAPSED__=$((END-START))s; exit $EC";
                    var (exitDv, outDv, errDv) = await _ssh.RunCommandAsync(cmdTimeDockerVersion, TimeSpan.FromSeconds(30), ct);
                    LogLine($"diag: docker version exit={exitDv}");
                    foreach (var l in (outDv ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }
                    foreach (var l in (errDv ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }

                    // 2) Measure responsiveness of Docker daemon: docker info
                    var cmdTimeDockerInfo = "START=$(date +%s); docker info; EC=$?; END=$(date +%s); echo __ELAPSED__=$((END-START))s; exit $EC";
                    var (exitDi, outDi, errDi) = await _ssh.RunCommandAsync(cmdTimeDockerInfo, TimeSpan.FromSeconds(45), ct);
                    LogLine($"diag: docker info exit={exitDi}");
                    foreach (var l in (outDi ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }
                    foreach (var l in (errDi ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }

                    // 3) Recent docker service logs (if journalctl available)
                    var cmdJournal = "journalctl -u docker -n 200 --no-pager 2>&1 || true";
                    var (_, outJ, _) = await _ssh.RunCommandAsync(cmdJournal, TimeSpan.FromSeconds(45), ct);
                    LogLine("diag: journalctl -u docker -n 200 --no-pager (last 200 lines):");
                    foreach (var l in (outJ ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }

                    // 4) Check docker.sock availability/queue
                    var cmdSs = "ss -xl 2>/dev/null | grep -i docker.sock || true";
                    var (_, outSs, _) = await _ssh.RunCommandAsync(cmdSs, TimeSpan.FromSeconds(20), ct);
                    LogLine("diag: ss -xl | grep docker.sock:");
                    foreach (var l in (outSs ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }

                    // 5) Current docker containers (full names)
                    var cmdPs = "docker ps -a --no-trunc | grep -i wireguard || true";
                    var (_, outPs, _) = await _ssh.RunCommandAsync(cmdPs, TimeSpan.FromSeconds(20), ct);
                    LogLine("diag: docker ps -a --no-trunc | grep -i wireguard:");
                    foreach (var l in (outPs ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }

                    // 6) iptables rules snapshot (first 50 lines)
                    var cmdIpt = "iptables -S 2>&1 | head -n 50";
                    var (_, outIpt, _) = await _ssh.RunCommandAsync(cmdIpt, TimeSpan.FromSeconds(20), ct);
                    LogLine("diag: iptables -S | head -n 50:");
                    foreach (var l in (outIpt ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }

                    var cmdIptNat = "iptables -t nat -S 2>&1 | head -n 50";
                    var (_, outIptNat, _) = await _ssh.RunCommandAsync(cmdIptNat, TimeSpan.FromSeconds(20), ct);
                    LogLine("diag: iptables -t nat -S | head -n 50:");
                    foreach (var l in (outIptNat ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }

                    // 7) UFW status
                    var cmdUfw = "ufw status verbose 2>&1 || true";
                    var (_, outUfw, _) = await _ssh.RunCommandAsync(cmdUfw, TimeSpan.FromSeconds(20), ct);
                    LogLine("diag: ufw status verbose:");
                    foreach (var l in (outUfw ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) LogLine(line); }
                }
                catch (Exception ex)
                {
                    LogLine($"diag: diagnostics block failed: {ex.Message}");
                }

                // --- WireGuard section ---
                LogLine("Preparing /opt/wireguard and docker-compose.yml...");
                await _ssh.EnsureDirectoryAsync("/opt/wireguard", ct);

                var publicIp = await _ssh.GetPublicIpAsync(ct);
                // Always generate compose with 7 peers as requested
                var compose = ComposeTemplate.Generate(publicIp, session.WgPort, 7);
                var composePath = "/opt/wireguard/docker-compose.yml";
                await _ssh.UploadTextAsync(composePath, compose, ct);

                LogLine("Starting WireGuard container...");
                var composeSelect = "COMPOSE=\"docker compose\"; docker compose version >/dev/null 2>&1 || COMPOSE=\"docker-compose\";";
                var pullCmd = composeSelect + " $COMPOSE -f /opt/wireguard/docker-compose.yml pull";
                await _ssh.RunCommandAsync(pullCmd, TimeSpan.FromMinutes(15), ct);
                // Background post-start network snapshot (runs after a short delay)
                var postStartDiag = "nohup sh -c \"sleep 3; { date; echo '--- ip route'; ip -4 route; echo '--- ip rule'; ip rule; echo '--- ip addr'; ip addr; echo '--- iptables'; iptables -S; echo '--- iptables nat'; iptables -t nat -S; } > /tmp/wg_after_start.txt 2>&1\" >/dev/null 2>&1 &";
                await _ssh.RunCommandAsync(postStartDiag, TimeSpan.FromSeconds(10), ct);
                // Failsafe: if the host becomes unreachable after starting the container,
                // this background job will remove the container after 3 minutes unless we signal success.
                var guardCmd = "nohup sh -c \"sleep 180; [ -f /tmp/wg_start_ok ] || docker rm -f wireguard\" >/dev/null 2>&1 &";
                await _ssh.RunCommandAsync(guardCmd, TimeSpan.FromSeconds(10), ct);
                var startCmd = composeSelect + " $COMPOSE -f /opt/wireguard/docker-compose.yml up -d";
                var (exitUp, stdoutUp, stderrUp) = await _ssh.RunCommandAsync(startCmd, TimeSpan.FromMinutes(10), ct);
                if (exitUp != 0)
                {
                    throw new Exception($"docker compose up failed: {stderrUp}\n{stdoutUp}");
                }

                // After starting the container, the host may reconfigure networking which can drop SSH.
                // Wait up to 150s for SSH port to become reachable again, then continue.
                LogLine("Waiting for SSH to stabilize after starting container...");
                var sshBack = await WaitForSshPortAsync(session.Host, session.Port, TimeSpan.FromSeconds(150), ct);
                if (!sshBack)
                {
                    LogLine("SSH did not recover within 150s. The failsafe will remove the container shortly.");
                    throw new Exception("SSH did not recover within the stabilization window after starting WireGuard.");
                }
                // Cancel the failsafe guard and dump post-start diagnostics (if any)
                await _ssh.RunCommandAsync("touch /tmp/wg_start_ok", TimeSpan.FromSeconds(10), ct);
                var (_, outPostDiag, _) = await _ssh.RunCommandAsync("sed -n '1,200p' /tmp/wg_after_start.txt 2>/dev/null || true", TimeSpan.FromSeconds(30), ct);
                if (!string.IsNullOrWhiteSpace(outPostDiag))
                {
                    LogLine("diag: post-start network snapshot (first 200 lines):");
                    foreach (var l in (outPostDiag ?? string.Empty).Split('\n'))
                    {
                        var line = l.TrimEnd();
                        if (!string.IsNullOrWhiteSpace(line)) LogLine(line);
                    }
                }

                // Verify container is running (with small backoff retries)
                LogLine("Verifying container is running...");
                var inspectCmd = "docker inspect -f '{{.State.Running}}' wireguard 2>/dev/null";
                int exitInspect = 0; string outInspect = string.Empty; string errInspect = string.Empty;
                var runningOk = false;
                for (int attempt = 0; attempt < 3 && !runningOk; attempt++)
                {
                    if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    (exitInspect, outInspect, errInspect) = await _ssh.RunCommandAsync(inspectCmd, TimeSpan.FromSeconds(120), ct);
                    // Log exactly what we inspected this attempt
                    var outTrim = (outInspect ?? string.Empty).Trim();
                    var errTrim = (errInspect ?? string.Empty).Trim();
                    LogLine($"inspect attempt {attempt + 1}: exit={exitInspect}, out='{outTrim}', err='{errTrim}'");
                    runningOk = exitInspect == 0 && string.Equals((outInspect ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase);
                }
                if (runningOk)
                {
                    var outTrim = (outInspect ?? string.Empty).Trim();
                    LogLine($"Verify OK: docker inspect indicates running (out='{outTrim}', exit={exitInspect})");
                    // Cancel the failsafe guard
                    await _ssh.RunCommandAsync("touch /tmp/wg_start_ok", TimeSpan.FromSeconds(10), ct);
                }
                if (!runningOk)
                {
                    // Show docker ps for context then fail
                    var (_, outPsErr, errPsErr) = await _ssh.RunCommandAsync("docker ps -a --filter name=^wireguard$", TimeSpan.FromSeconds(60), ct);
                    throw new Exception($"WireGuard container is not running: {errInspect}\ninspect: {outInspect}\nps: {errPsErr}\n{outPsErr}");
                }
                // Stage 4: WireGuard successfully started
                SuccessMsg("WireGuard started");

                // --- Export entire WireGuard folder to local export path ---
                LogLine($"Exporting /opt/wireguard to local folder: {session.ExportPath} ...");
                // Clean local export folder completely before copying
                try
                {
                    if (Directory.Exists(session.ExportPath))
                    {
                        Directory.Delete(session.ExportPath, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    LogLine($"WARN: failed to fully delete export folder: {ex.Message}");
                }
                try { Directory.CreateDirectory(session.ExportPath); } catch { }
                var localWireguardRoot = Path.Combine(session.ExportPath, "wireguard");
                try { Directory.CreateDirectory(localWireguardRoot); } catch { }
                await _ssh.DownloadDirectoryAsync("/opt/wireguard", localWireguardRoot, ct);
                try
                {
                    var confs = Directory.GetFiles(session.ExportPath, "*.conf", SearchOption.AllDirectories);
                    if (confs.Length > 0)
                    {
                        LogLine("Exported .conf files:");
                        foreach (var p in confs) LogLine(p);
                    }
                }
                catch { }
                SuccessMsg($"WireGuard folder exported to: {session.ExportPath}");

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
