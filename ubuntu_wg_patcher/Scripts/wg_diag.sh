#!/usr/bin/env bash
# WireGuard deep-diagnose for Ubuntu 20+ (read-only)
# Produces a complete report to /tmp/wg_diag_YYYYmmdd_HHMMSS/report.txt

set -u
LANG=C
PATH=/usr/sbin:/sbin:/usr/bin:/bin:/usr/local/bin

TS="$(date +%Y%m%d_%H%M%S)"
OUTDIR="/tmp/wg_diag_${TS}"
LOG="${OUTDIR}/report.txt"
mkdir -p "${OUTDIR}"

redact_keys() {
  # Redact PrivateKey/PresharedKey in configs
  sed -E 's/(PrivateKey|PresharedKey)[[:space:]]*=[[:space:]]*[A-Za-z0-9+\/=]+/\1 = <REDACTED>/g'
}

hr() { printf '\n===== %s =====\n' "$*" | tee -a "${LOG}"; }
run() {
  printf '>>> %s\n' "$*" | tee -a "${LOG}"
  # shellcheck disable=SC2086
  bash -o pipefail -c "$*" 2>&1 | tee -a "${LOG}" || true
  printf '\n' | tee -a "${LOG}"
}

note() { printf 'NOTE: %s\n\n' "$*" | tee -a "${LOG}"; }

finish() {
  printf '\nReport saved to: %s\n' "${LOG}"
  printf 'Extra files (configs/raw dumps) in: %s\n' "${OUTDIR}"
}
trap finish EXIT

hr "BASIC INFO"
run "date -Is"
run "hostnamectl 2>/dev/null || true"
run "uname -a"
run "test -r /etc/os-release && cat /etc/os-release | tee ${OUTDIR}/os-release.txt >/dev/null"
run "lsb_release -a 2>/dev/null || true"
run "systemd-detect-virt 2>/dev/null || true"

hr "PACKAGE VERSIONS"
run "apt-cache policy wireguard 2>/dev/null || true"
run "dpkg -l | egrep -i 'wireguard|wg-quick|iproute2|iptables|nftables|ufw|linux-image|linux-headers' || true"
run "wg --version 2>/dev/null || true"
run "wg-quick --version 2>/dev/null || true"
run "iptables --version 2>/dev/null || true"
run "nft --version 2>/dev/null || true"
run "ip -V 2>/dev/null || true"

hr "KERNEL & MODULE"
run "uname -r"
run "lsmod | egrep -i '^wireguard|^udp|^nf|^ip' || true"
run "modinfo wireguard 2>/dev/null || true"
run "dmesg --ctime | egrep -i 'wireguard|wg0|udp|nfnetlink' | tail -n 200 || true"

hr "NETWORK SNAPSHOT"
run "ip -br link"
run "ip -br addr"
run "ip -4 route show table main"
run "ip -6 route show table main"
run "ip rule show"
run "sysctl -a 2>/dev/null | egrep -i 'net.ipv4.ip_forward|net.ipv6.conf.all.forwarding|net.ipv4.conf.all.rp_filter'"

hr "DNS/RESOLVER"
run "resolvectl status 2>/dev/null || systemd-resolve --status 2>/dev/null || true"
run "cat /etc/resolv.conf"

hr "PROCESSES/LISTENERS"
run "ps aux | egrep -i '[w]g|wireguard|amnezia|outline|xray|v2ray' || true"
run "ss -ulpn || true"
run "ss -tulpn || true"

hr "FIREWALL DETECTION"
run "update-alternatives --query iptables 2>/dev/null || true"
run "iptables -S 2>/dev/null || true"
run "ip6tables -S 2>/dev/null || true"
run "iptables -t nat -S 2>/dev/null || true"
run "ip6tables -t nat -S 2>/dev/null || true"
run "nft list ruleset 2>/dev/null || true"
run "ufw status verbose 2>/dev/null || true"
run "systemctl is-active firewalld 2>/dev/null || true"

hr "WIREGUARD CONFIGS"
CONF_DIR="/etc/wireguard"
CONF_LIST=()
if ls ${CONF_DIR}/*.conf >/dev/null 2>&1; then
  for c in ${CONF_DIR}/*.conf; do
    base="$(basename "$c")"
    CONF_LIST+=("$c")
    printf '--- %s ---\n' "$c" | tee -a "${LOG}"
    sed -n '1,200p' "$c" | redact_keys | tee "${OUTDIR}/${base}.redacted.conf" | tee -a "${LOG}" >/dev/null
    printf '\n' | tee -a "${LOG}"
  done
else
  note "No WireGuard configs in ${CONF_DIR}"
fi

# Discover interfaces from configs + env
declare -a IFACES
if [ -n "${WG_IFACES-}" ]; then
  # user-provided list
  read -r -a IFACES <<< "${WG_IFACES}"
else
  while IFS= read -r f; do
    name="$(basename "$f" .conf)"
    IFACES+=("$name")
  done < <(ls ${CONF_DIR}/*.conf 2>/dev/null || true)
fi
# Fallback: infer from running devices (wg show)
if [ ${#IFACES[@]} -eq 0 ] && command -v wg >/dev/null 2>&1; then
  while read -r dev; do
    [ -n "$dev" ] && IFACES+=("$dev")
  done < <(wg show interfaces 2>/dev/null | tr ' ' '\n')
fi

# Build port set (from confs or env)
declare -a PORTS
if [ -n "${WG_UDP_PORTS-}" ]; then
  read -r -a PORTS <<< "${WG_UDP_PORTS}"
else
  # Parse ListenPort from confs
  for c in "${CONF_LIST[@]}"; do
    p="$(awk -F= '/^[[:space:]]*ListenPort[[:space:]]*=/{gsub(/ /,"",$2);print $2}' "$c")"
    [ -n "$p" ] && PORTS+=("$p")
  done
  # Also from ss
  while read -r p; do
    PORTS+=("$p")
  done < <(ss -ulpn 2>/dev/null | awk '/\*/{print $5} /udp/{print $5}' | sed -n 's/.*:\([0-9][0-9]*\)$/\1/p' | sort -u)
fi
# Deduplicate PORTS
if [ ${#PORTS[@]} -gt 0 ]; then
  mapfile -t PORTS < <(printf "%s\n" "${PORTS[@]}" | awk 'NF && !seen[$0]++' | sort -n)
fi

hr "WG-QUICK / SYSTEMD STATUS"
if [ ${#IFACES[@]} -gt 0 ]; then
  for i in "${IFACES[@]}"; do
    run "systemctl status --no-pager -l wg-quick@${i}.service 2>/dev/null || true"
  done
else
  note "No wg-quick@<iface> units inferred."
fi

hr "WG RUNTIME STATE"
if command -v wg >/dev/null 2>&1; then
  run "wg show"
  run "wg show all dump"
else
  note "'wg' command not found."
fi

hr "ROUTING TABLES (ALL)"
run "ip -4 route show table all"
run "ip -6 route show table all"

hr "RT RULES PER IFACE"
for i in "${IFACES[@]:-}"; do
  [ -z "$i" ] && continue
  run "ip rule show | sed -n '1,200p'"
  run "ip -4 route show table ${i} 2>/dev/null || true"
  run "ip -6 route show table ${i} 2>/dev/null || true"
done

hr "INPUT COUNTERS FOR WG PORTS"
if [ ${#PORTS[@]} -gt 0 ]; then
  printf 'Ports considered: %s\n\n' "${PORTS[*]}" | tee -a "${LOG}"
  run "iptables -L INPUT -v -n 2>/dev/null | egrep -i 'udp|${PORTS[*]// /|}' || true"
  run "ip6tables -L INPUT -v -n 2>/dev/null | egrep -i 'udp|${PORTS[*]// /|}' || true"
else
  note "No UDP ports deduced (no ListenPort in confs and none visible in ss)."
fi

hr "NAT/MASQUERADE (IF ROUTING PEERS TO INTERNET)"
run "iptables -t nat -L -v -n 2>/dev/null || true"
run "ip6tables -t nat -L -v -n 2>/dev/null || true"
run "nft list table ip nat 2>/dev/null || true"
run "nft list table ip6 nat 2>/dev/null || true"

hr "JOURNAL (RECENT)"
if [ ${#IFACES[@]} -gt 0 ]; then
  for i in "${IFACES[@]}"; do
    run "journalctl -u wg-quick@${i} --no-pager --since '-24h' | tail -n 400"
  done
else
  run "journalctl --no-pager -k | egrep -i 'wireguard|wg0|udp' | tail -n 400 || true"
fi

hr "CONNECTIVITY SELF-TESTS (NON-DESTURCTIVE)"
# External IPs (if Internet available)
if command -v curl >/dev/null 2>&1; then
  run "curl -4s https://ipv4.icanhazip.com || true"
  run "curl -6s https://ipv6.icanhazip.com || true"
else
  note "curl not found; skipping external IP check."
fi
# UDP reachability smoke test (DNS) – proves outbound UDP works
if command -v dig >/dev/null 2>&1; then
  run "dig +time=2 +tries=1 @1.1.1.1 google.com A || true"
else
  note "dig not found; skipping DNS/UDP smoke test."
fi
# Local UDP listening check for WG ports
if [ ${#PORTS[@]} -gt 0 ]; then
  for p in "${PORTS[@]}"; do
    run "ss -ulpn | egrep ":${p}[[:space:]]" || true"
  done
fi

hr "SYSTEMD UNIT ENUM"
run "systemctl list-units 'wg-quick@*' --all --no-pager || true"

hr "ETC/WIREGUARD RAW (REDACTED COPIES SAVED)"
if ls ${CONF_DIR}/*.conf >/dev/null 2>&1; then
  for c in ${CONF_DIR}/*.conf; do
    base="$(basename "$c")"
    sed -n '1,999p' "$c" | redact_keys > "${OUTDIR}/${base}.redacted.conf"
  done
  printf "Redacted copies saved in %s\n\n" "${OUTDIR}" | tee -a "${LOG}"
fi

hr "DONE"
echo "If one server works and another doesn't, differences will likely appear in:" | tee -a "${LOG}"
echo "- Listening UDP port presence (ss -ulpn) / iptables/nft allow rules" | tee -a "${LOG}"
echo "- ip_forward/rp_filter sysctls and per-iface routes/rules" | tee -a "${LOG}"
echo "- wg-quick@<iface> unit failures in journal" | tee -a "${LOG}"
echo "- NAT/MASQUERADE rules if server routes peers to Internet" | tee -a "${LOG}"
