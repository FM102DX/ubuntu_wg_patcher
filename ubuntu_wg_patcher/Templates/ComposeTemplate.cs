using System.Text;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Templates
{
    public static class ComposeTemplate
    {
        public static string Generate(string serverPublicIp, int wgPort, int peers)
        {
            var sb = new StringBuilder();
            // Emit the exact docker-compose provided by the user (with substitutions for port/peers)
            sb.AppendLine("version: \"2.1\"");
            sb.AppendLine("services:");
            sb.AppendLine("  wireguard:");
            sb.AppendLine("    image: lscr.io/linuxserver/wireguard");
            sb.AppendLine("    container_name: wireguard");
            sb.AppendLine("    cap_add:");
            sb.AppendLine("      - NET_ADMIN");
            sb.AppendLine("      - SYS_MODULE");
            sb.AppendLine("    environment:");
            sb.AppendLine("      - PUID=1000");
            sb.AppendLine("      - PGID=1000");
            sb.AppendLine("      - TZ=Europe/London");
            sb.AppendLine("      - SERVERURL=auto");
            sb.AppendLine($"      - SERVERPORT={wgPort}");
            // Deterministic behavior: peers will be provisioned by our code, not by the container
            sb.AppendLine("      - PEERS=0");
            sb.AppendLine("      - PEERDNS=auto");
            sb.AppendLine("      - INTERNAL_SUBNET=10.13.13.0");
            sb.AppendLine("      - ALLOWEDIPS=0.0.0.0/0");
            sb.AppendLine("    volumes:");
            sb.AppendLine("      - ~/wireguard/config:/config");
            sb.AppendLine("      - /lib/modules:/lib/modules");
            sb.AppendLine("    ports:");
            sb.AppendLine($"      - {wgPort}:{wgPort}/udp");
            sb.AppendLine("    sysctls:");
            sb.AppendLine("      - net.ipv4.conf.all.src_valid_mark=1");
            sb.AppendLine("    restart: unless-stopped");
            return sb.ToString();
        }
    }
}
