using System.Text;

namespace ubuntu_wg_patcher.Services
{
    public static class RemoteScripts
    {
        public static string BuildPreflightScript(int wgPort, bool disableIPv6)
        {
            var sb = new StringBuilder();
            // Minimal diagnostics only; other steps kept commented for later
            sb.AppendLine("set -o pipefail 2>/dev/null || true");
            sb.AppendLine("export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
            sb.AppendLine("echo '--- System diagnostics ---'");
            sb.AppendLine("OS_NAME=$( (lsb_release -ds 2>/dev/null) || (awk -F= '$1==\"PRETTY_NAME\"{print $2}' /etc/os-release 2>/dev/null | tr -d '\"') || uname -a )");
            sb.AppendLine("echo \"OS: $OS_NAME\"");
            sb.AppendLine("if command -v docker >/dev/null 2>&1; then echo \"Docker: $(docker --version 2>&1)\"; else echo 'ERROR: Docker not installed' >&2; fi");
            sb.AppendLine("if docker compose version >/dev/null 2>&1; then echo \"Compose: $(docker compose version 2>&1)\"; elif command -v docker-compose >/dev/null 2>&1; then echo \"Compose: $(docker-compose --version 2>&1)\"; else echo 'ERROR: Compose not installed' >&2; fi");

            sb.AppendLine("# --- Original steps (commented out for incremental rollout) ---");
            sb.AppendLine("# export DEBIAN_FRONTEND=noninteractive");
            sb.AppendLine("# if [ \"$(id -u)\" -ne 0 ]; then echo 'Please run as root'; exit 1; fi");
            sb.AppendLine("# apt-get update -y");
            sb.AppendLine("# apt-get install -y curl iptables-persistent jq ufw >/dev/null 2>&1 || true");
            sb.AppendLine("# if ! command -v docker >/dev/null 2>&1; then curl -fsSL https://get.docker.com | sh; fi");
            sb.AppendLine("# if ! command -v docker >/dev/null 2>&1; then apt-get install -y docker.io || true; fi");
            sb.AppendLine("# systemctl enable --now docker 2>/dev/null || service docker start 2>/dev/null || true");
            sb.AppendLine("# mkdir -p /opt/wireguard");
            sb.AppendLine("cat >/etc/sysctl.d/99-wg.conf <<'EOF'");
            sb.AppendLine("net.ipv4.ip_forward=1");
            sb.AppendLine("net.ipv4.conf.all.src_valid_mark=1");
            if (disableIPv6)
            {
                sb.AppendLine("net.ipv6.conf.all.disable_ipv6=1");
                sb.AppendLine("net.ipv6.conf.default.disable_ipv6=1");
            }
            sb.AppendLine("EOF");
            sb.AppendLine("sysctl -p /etc/sysctl.d/99-wg.conf || true");
            // Tune SSH server keep-alive/timeouts to prevent idle disconnects
            sb.AppendLine("# Tune SSH server keep-alive/timeouts to prevent idle drops");
            sb.AppendLine("if [ -f /etc/ssh/sshd_config ]; then");
            sb.AppendLine("  cp -n /etc/ssh/sshd_config /etc/ssh/sshd_config.bak 2>/dev/null || true");
            sb.AppendLine("  if grep -qE '^#?ClientAliveInterval' /etc/ssh/sshd_config; then sed -i -E 's/^#?ClientAliveInterval\\s+.*/ClientAliveInterval 30/' /etc/ssh/sshd_config; else echo 'ClientAliveInterval 30' >> /etc/ssh/sshd_config; fi");
            sb.AppendLine("  if grep -qE '^#?ClientAliveCountMax' /etc/ssh/sshd_config; then sed -i -E 's/^#?ClientAliveCountMax\\s+.*/ClientAliveCountMax 6/' /etc/ssh/sshd_config; else echo 'ClientAliveCountMax 6' >> /etc/ssh/sshd_config; fi");
            sb.AppendLine("  if grep -qE '^#?TCPKeepAlive' /etc/ssh/sshd_config; then sed -i -E 's/^#?TCPKeepAlive\\s+.*/TCPKeepAlive yes/' /etc/ssh/sshd_config; else echo 'TCPKeepAlive yes' >> /etc/ssh/sshd_config; fi");
            sb.AppendLine("  systemctl reload sshd 2>/dev/null || systemctl reload ssh 2>/dev/null || service ssh reload 2>/dev/null || service sshd reload 2>/dev/null || true");
            sb.AppendLine("fi");
            sb.AppendLine("# IFACE=$(ip -4 route ls default | awk '{print $5}' | head -n1)");
            sb.AppendLine("# if ! iptables -t nat -C POSTROUTING -s 10.13.13.0/24 -o \"$IFACE\" -j MASQUERADE 2>/dev/null; then");
            sb.AppendLine("#   iptables -t nat -A POSTROUTING -s 10.13.13.0/24 -o \"$IFACE\" -j MASQUERADE");
            sb.AppendLine("# fi");
            sb.AppendLine("# if command -v netfilter-persistent >/dev/null 2>&1; then netfilter-persistent save; else iptables-save > /etc/iptables/rules.v4 || true; fi");
            sb.AppendLine("# if ufw status | grep -qi active; then ufw allow " + wgPort + "/udp || true; fi");
            sb.AppendLine("# if command -v apt-get >/dev/null 2>&1; then apt-get install -y docker-compose-plugin >/dev/null 2>&1 || apt-get install -y docker-compose >/dev/null 2>&1 || true; fi");
            sb.AppendLine("# COMPOSE_CMD=docker compose");
            sb.AppendLine("# if ! docker compose version >/dev/null 2>&1; then");
            sb.AppendLine("#   if command -v docker-compose >/dev/null 2>&1; then COMPOSE_CMD=docker-compose; fi");
            sb.AppendLine("# fi");
            sb.AppendLine("# echo \"$COMPOSE_CMD\" > /opt/wireguard/.compose_cmd");
            sb.AppendLine("# if ! command -v docker >/dev/null 2>&1; then echo 'Docker installation failed or not in PATH' >&2; exit 1; fi");

            return sb.ToString();
        }
    }
}
