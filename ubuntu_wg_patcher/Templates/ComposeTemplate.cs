using System.Text;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Templates
{
    public static class ComposeTemplate
    {
        public static string Generate(string serverPublicIp, int wgPort, int peers)
        {
            var sb = new StringBuilder();
            sb.AppendLine("services:");
            sb.AppendLine("  wireguard:");
            sb.AppendLine("    image: lscr.io/linuxserver/wireguard:latest");
            sb.AppendLine("    container_name: wireguard");
            sb.AppendLine("    network_mode: \"host\"");
            sb.AppendLine("    cap_add:");
            sb.AppendLine("      - NET_ADMIN");
            sb.AppendLine("      - SYS_MODULE");
            sb.AppendLine("    environment:");
            sb.AppendLine("      - PUID=0");
            sb.AppendLine("      - PGID=0");
            sb.AppendLine("      - TZ=UTC");
            sb.AppendLine($"      - SERVERURL={serverPublicIp}");
            sb.AppendLine($"      - SERVERPORT={wgPort}");
            sb.AppendLine($"      - PEERS={peers}");
            sb.AppendLine("      - INTERNAL_SUBNET=10.13.13.0/24");
            sb.AppendLine("      - ALLOWEDIPS=0.0.0.0/0");
            sb.AppendLine("    volumes:");
            sb.AppendLine("      - /opt/wireguard/config:/config");
            sb.AppendLine("    restart: unless-stopped");
            return sb.ToString();
        }
    }
}
