using System.Text;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Templates
{
    public static class ComposeTemplate
    {
        public static string Generate(string serverPublicIp, int wgPort, int peers)
        {
            var sb = new StringBuilder();
            // Emit exact docker-compose requested by the user
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
            sb.AppendLine("      - SERVERURL=62.60.179.118");
            sb.AppendLine("      - SERVERPORT=443");
            sb.AppendLine("      - PEERS=peer7");
            sb.AppendLine("      - PEERDNS=1.1.1.1,8.8.8.8");
            sb.AppendLine("      - INTERNAL_SUBNET=10.13.13.0/24");
            sb.AppendLine("      - ALLOWEDIPS=0.0.0.0/0");
            sb.AppendLine("    volumes:");
            sb.AppendLine("      - /opt/wireguard/config:/config");
            sb.AppendLine("      - /lib/modules:/lib/modules");
            sb.AppendLine("    sysctls:");
            sb.AppendLine("      - net.ipv4.conf.all.src_valid_mark=1");
            sb.AppendLine("      - net.ipv4.ip_forward=1");
            sb.AppendLine("      - net.ipv6.conf.all.disable_ipv6=1");
            sb.AppendLine("      - net.ipv6.conf.default.disable_ipv6=1");
            sb.AppendLine("    restart: unless-stopped");
            return sb.ToString();
        }
    }
}
