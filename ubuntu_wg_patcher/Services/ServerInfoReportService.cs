using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Services
{
    public class ServerInfoReportService : IServerInfoReportService
    {
        private readonly ISshClientService _ssh;
        public ServerInfoReportService(ISshClientService ssh)
        {
            _ssh = ssh;
        }

        public async Task<string> CollectWireGuardServrinfoAsync(SessionParams cfg, IProgress<string> progress, CancellationToken ct)
        {
            void Log(string m) => progress.Report(m);
            var sb = new StringBuilder();
            sb.AppendLine("=== Servrinfo: WireGuard (keys & peers) ===");

            try
            {
                // Ensure connection is alive; DiagnosticsService already connected, so we just proceed
                var (exRun, outRun, _) = await _ssh.RunCommandAsync("docker inspect -f '{{.State.Running}}' wireguard 2>/dev/null", TimeSpan.FromSeconds(10), ct);
                var isRunning = exRun == 0 && string.Equals((outRun ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase);
                if (!isRunning)
                {
                    sb.AppendLine("container: not running");
                    return sb.ToString();
                }

                // Gather interface and peers
                var (_, wgShow, _) = await _ssh.RunCommandAsync("docker exec wireguard sh -lc 'wg show 2>/dev/null || true'", TimeSpan.FromSeconds(30), ct);
                var (_, wgAddr, _) = await _ssh.RunCommandAsync("docker exec wireguard sh -lc 'ip -o addr show wg0 2>/dev/null || true'", TimeSpan.FromSeconds(10), ct);
                var (_, contIp, _) = await _ssh.RunCommandAsync("docker inspect -f '{{range.NetworkSettings.Networks}}{{.IPAddress}}{{end}}' wireguard 2>/dev/null", TimeSpan.FromSeconds(10), ct);
                var (_, wgShowConf, _) = await _ssh.RunCommandAsync("docker exec wireguard sh -lc 'wg showconf wg0 2>/dev/null || true'", TimeSpan.FromSeconds(30), ct);

                // Detect PSK presence per peer public key (do NOT output values)
                var pskByPeer = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(wgShowConf))
                {
                    string? curPeerPub = null;
                    foreach (var raw in wgShowConf.Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.StartsWith("[Peer]", StringComparison.OrdinalIgnoreCase)) { curPeerPub = null; continue; }
                        if (line.StartsWith("PublicKey", StringComparison.OrdinalIgnoreCase))
                        {
                            var idx = line.IndexOf('=');
                            if (idx >= 0) curPeerPub = line[(idx + 1)..].Trim();
                        }
                        else if (line.StartsWith("PresharedKey", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(curPeerPub)) pskByPeer.Add(curPeerPub);
                        }
                    }
                }

                // Parse wg show
                string ifacePub = string.Empty;
                string listenPort = string.Empty;
                var peers = new System.Collections.Generic.List<(string Pub, string Allowed, string Endpoint, string Handshake, string Rx, string Tx, string Keepalive, bool Psk)>();

                string? curPub = null; string allowed = string.Empty; string endpoint = string.Empty; string hs = string.Empty; string rx = "0B"; string tx = "0B"; string keepalive = "off";

                foreach (var raw in (wgShow ?? string.Empty).Split('\n'))
                {
                    var line = raw.Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith("public key:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ifacePub))
                    {
                        ifacePub = line.Split(':', 2)[1].Trim();
                        continue;
                    }
                    if (line.StartsWith("listening port:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(listenPort))
                    {
                        listenPort = line.Split(':', 2)[1].Trim();
                        continue;
                    }

                    if (line.StartsWith("peer:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(curPub))
                            peers.Add((curPub, allowed, endpoint, hs, rx, tx, keepalive, pskByPeer.Contains(curPub)));

                        curPub = line.Split(':', 2)[1].Trim();
                        allowed = string.Empty; endpoint = string.Empty; hs = string.Empty; rx = "0B"; tx = "0B"; keepalive = "off";
                        continue;
                    }

                    if (curPub != null)
                    {
                        if (line.StartsWith("allowed ips:", StringComparison.OrdinalIgnoreCase))
                            allowed = line.Split(':', 2)[1].Trim();
                        else if (line.StartsWith("endpoint:", StringComparison.OrdinalIgnoreCase))
                            endpoint = line.Split(':', 2)[1].Trim();
                        else if (line.StartsWith("latest handshake:", StringComparison.OrdinalIgnoreCase))
                            hs = line.Split(':', 2)[1].Trim();
                        else if (line.StartsWith("transfer:", StringComparison.OrdinalIgnoreCase))
                        {
                            var m = Regex.Match(line, @"transfer:\s*(.+?)\s+received,\s+(.+?)\s+sent", RegexOptions.IgnoreCase);
                            if (m.Success) { rx = m.Groups[1].Value.Trim(); tx = m.Groups[2].Value.Trim(); }
                        }
                        else if (line.StartsWith("persistent keepalive:", StringComparison.OrdinalIgnoreCase))
                            keepalive = line.Split(':', 2)[1].Trim();
                    }
                }
                if (!string.IsNullOrWhiteSpace(curPub))
                    peers.Add((curPub!, allowed, endpoint, hs, rx, tx, keepalive, pskByPeer.Contains(curPub!)));

                // Addresses on wg0
                var addrList = new System.Collections.Generic.List<string>();
                foreach (var raw in (wgAddr ?? string.Empty).Split('\n'))
                {
                    var l = raw.Trim(); if (string.IsNullOrWhiteSpace(l)) continue;
                    var m = Regex.Match(l, @"\binet6?\s+(\S+)");
                    if (m.Success) addrList.Add(m.Groups[1].Value);
                }

                // Build output block (do not include private keys or PSK values)
                sb.AppendLine("[Interface]");
                sb.AppendLine($"  PublicKey: {(string.IsNullOrWhiteSpace(ifacePub) ? "(none)" : ifacePub)}");
                sb.AppendLine($"  ListenPort: {(string.IsNullOrWhiteSpace(listenPort) ? "(none)" : listenPort)}");
                sb.AppendLine($"  WgAddresses: {(addrList.Count > 0 ? string.Join(", ", addrList) : "(none)")}");
                var contIpTrim = (contIp ?? string.Empty).Trim();
                sb.AppendLine($"  ContainerIP: {(string.IsNullOrWhiteSpace(contIpTrim) ? "(none)" : contIpTrim)}");
                sb.AppendLine();

                sb.AppendLine($"[Peers] (N={peers.Count})");
                foreach (var p in peers)
                {
                    sb.AppendLine($"  - PeerPublicKey: {p.Pub}");
                    sb.AppendLine($"    AllowedIPs: {(string.IsNullOrWhiteSpace(p.Allowed) ? "(none)" : p.Allowed)}");
                    sb.AppendLine($"    Endpoint: {(string.IsNullOrWhiteSpace(p.Endpoint) ? "(none)" : p.Endpoint)}");
                    sb.AppendLine($"    LatestHandshake: {(string.IsNullOrWhiteSpace(p.Handshake) ? "never" : p.Handshake)}");
                    sb.AppendLine($"    Transfer: rx={p.Rx} tx={p.Tx}");
                    sb.AppendLine($"    PersistentKeepalive: {(string.IsNullOrWhiteSpace(p.Keepalive) ? "off" : p.Keepalive)}");
                    sb.AppendLine($"    PresharedKeyPresent: {(p.Psk ? "true" : "false")}");
                }

                var allKeys = string.Join(", ", peers.Select(x => x.Pub));
                sb.AppendLine();
                sb.AppendLine("PeerPublicKeys (comma-separated):");
                sb.AppendLine(allKeys);
            }
            catch (Exception ex)
            {
                Log($"WARN: Servrinfo collection failed: {ex.Message}");
            }

            return sb.ToString();
        }
    }
}
