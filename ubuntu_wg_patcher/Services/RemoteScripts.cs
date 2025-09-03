using System.Text;

namespace ubuntu_wg_patcher.Services
{
    public static class RemoteScripts
    {
        public static string BuildPreflightScript(int wgPort, bool disableIPv6)
        {
            var sb = new StringBuilder();
            sb.AppendLine("set -euo pipefail");
            sb.AppendLine("export DEBIAN_FRONTEND=noninteractive");
            sb.AppendLine("export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
            sb.AppendLine("if [ \"$(id -u)\" -ne 0 ]; then echo 'Please run as root'; exit 1; fi");
            sb.AppendLine("apt-get update -y");
            sb.AppendLine("apt-get install -y curl iptables-persistent jq ufw >/dev/null 2>&1 || true");
            sb.AppendLine("# Install Docker if missing (try official convenience script, then distro package as fallback)");
            sb.AppendLine("if ! command -v docker >/dev/null 2>&1; then curl -fsSL https://get.docker.com | sh; fi");
            sb.AppendLine("if ! command -v docker >/dev/null 2>&1; then apt-get install -y docker.io || true; fi");
            sb.AppendLine("# Ensure docker service is running");
            sb.AppendLine("systemctl enable --now docker 2>/dev/null || service docker start 2>/dev/null || true");

            sb.AppendLine("mkdir -p /opt/wireguard");

            // sysctl
            sb.AppendLine("cat >/etc/sysctl.d/99-wg.conf <<'EOF'");
            sb.AppendLine("net.ipv4.ip_forward=1");
            if (disableIPv6)
            {
                sb.AppendLine("net.ipv6.conf.all.disable_ipv6=1");
                sb.AppendLine("net.ipv6.conf.default.disable_ipv6=1");
            }
            sb.AppendLine("EOF");
            sb.AppendLine("sysctl -p /etc/sysctl.d/99-wg.conf || true");

            // NAT
            sb.AppendLine("IFACE=$(ip -4 route ls default | awk '{print $5}' | head -n1)");
            sb.AppendLine("if ! iptables -t nat -C POSTROUTING -s 10.13.13.0/24 -o \"$IFACE\" -j MASQUERADE 2>/dev/null; then");
            sb.AppendLine("  iptables -t nat -A POSTROUTING -s 10.13.13.0/24 -o \"$IFACE\" -j MASQUERADE");
            sb.AppendLine("fi");
            sb.AppendLine("if command -v netfilter-persistent >/dev/null 2>&1; then netfilter-persistent save; else iptables-save > /etc/iptables/rules.v4 || true; fi");

            // UFW
            sb.AppendLine("if ufw status | grep -qi active; then ufw allow " + wgPort + "/udp || true; fi");

            // Compose command helper written to file for reuse
            sb.AppendLine("# Try to ensure compose availability (plugin or legacy)");
            sb.AppendLine("if command -v apt-get >/dev/null 2>&1; then apt-get install -y docker-compose-plugin >/dev/null 2>&1 || apt-get install -y docker-compose >/dev/null 2>&1 || true; fi");
            sb.AppendLine("COMPOSE_CMD=docker compose");
            sb.AppendLine("if ! docker compose version >/dev/null 2>&1; then");
            sb.AppendLine("  if command -v docker-compose >/dev/null 2>&1; then COMPOSE_CMD=docker-compose; fi");
            sb.AppendLine("fi");
            sb.AppendLine("echo \"$COMPOSE_CMD\" > /opt/wireguard/.compose_cmd");

            // Final check: ensure docker exists
            sb.AppendLine("if ! command -v docker >/dev/null 2>&1; then echo 'Docker installation failed or not in PATH' >&2; exit 1; fi");

            return sb.ToString();
        }
    }
}
