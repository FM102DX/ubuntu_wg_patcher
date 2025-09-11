using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using ubuntu_wg_patcher.Logging;
using ubuntu_wg_patcher.Models;
using ubuntu_wg_patcher.Templates;

namespace ubuntu_wg_patcher.Services
{
    public class WireGuardRunner : IConfigRunner
    {
        private readonly ISshClientService _ssh;

        public WireGuardRunner(ISshClientService ssh)
        {
            _ssh = ssh;
        }

        private async Task ProvisionPeersWithoutPskAsync(SessionParams session, Action<string> log, Action<string> success, CancellationToken ct)
        {
            // Что делает: детерминированно провиженит N пиров без PSK внутри контейнера, делает сохранение конфигурации и клиентские peer{i}.conf.
            // Зачем: устранить зависимость от автогенерации пиров образа; всегда Policy: PSK=off.
            var n = Math.Max(0, session.Peers);
            if (n == 0)
            {
                log("Provision: peers count is 0 — skipping peer provisioning.");
                return;
            }

            log($"Provision: generating {n} peers (PSK=off)...");

            // Получим публичный IP ещё раз (для Endpoint). На случай NAT подмены он совпадает с ранее использованным.
            var publicIp = await _ssh.GetPublicIpAsync(ct);

            // Склепаем скрипт, который внутри контейнера:
            //  - очищает любые существующие peers (wg set wg0 peer ... remove) и peer*/
            //  - извлекает server public key и listening port из wg show
            //  - генерирует peer{i} ключи/папки, добавляет peer в wg0 (AllowedIPs=10.13.13.{i+1}/32)
            //  - апдейтит /config/wg0.conf (только [Peer] секции) без PresharedKey
            //  - пишет /config/peer{i}/peer{i}.conf для клиента (без PSK), Endpoint=<publicIp>:<WgPort>
            //  - чистит любые PresharedKey из конфигов (на всякий случай)
            var script = $$"""
set -e
srv_pub="$(wg show | awk -F': ' "/^public key:/{print $2; exit}")" || true
listen_port="$(wg show | awk -F': ' "/^listening port:/{print $2; exit}")" || true
mkdir -p /config
# remove existing peer dirs and peers from runtime
find /config -maxdepth 1 -type d -name "peer*" -exec rm -rf {} + 2>/dev/null || true
for k in $(wg show wg0 peers 2>/dev/null); do wg set wg0 peer "$k" remove || true; done
# strip all [Peer] sections from wg0.conf, keep only [Interface]
if [ -f /config/wg0.conf ]; then
  awk "BEGIN{s=1} /^\\[Peer\\]$/{s=0} s==1 {print}" /config/wg0.conf > /tmp/wg0_iface.conf || true
  if [ -s /tmp/wg0_iface.conf ]; then cp /tmp/wg0_iface.conf /config/wg0.conf; fi
fi

PEERS={{n}}
for i in $(seq 1 $PEERS); do
  dir="/config/peer${i}"; mkdir -p "$dir"
  priv="$(wg genkey)"; pub="$(printf "%s" "$priv" | wg pubkey)"
  echo "$priv" > "$dir/peer${i}.key"; chmod 600 "$dir/peer${i}.key"
  echo "$pub" > "$dir/peer${i}.pub"
  addr=$((i+1))
  # runtime add on server
  wg set wg0 peer "$pub" allowed-ips 10.13.13.${addr}/32
  # persist on server
  printf "\n[Peer]\nPublicKey=%s\nAllowedIPs=10.13.13.%s/32\n" "$pub" "$addr" >> /config/wg0.conf
  # client config (no PSK)
  cat > "$dir/peer${i}.conf" <<EOF
[Interface]
PrivateKey=$priv
Address=10.13.13.${addr}/32
DNS=1.1.1.1

[Peer]
PublicKey=${srv_pub}
AllowedIPs=0.0.0.0/0
Endpoint={{publicIp}}:{{session.WgPort}}
PersistentKeepalive=25
EOF
done

# ensure no PSK lines anywhere
sed -i "/^PresharedKey/d" /config/wg0.conf 2>/dev/null || true
find /config -type f -name "*.conf" -exec sed -i "/^PresharedKey/d" {} + 2>/dev/null || true
""";

            // Передадим скрипт в контейнер через base64, затем выполним
            // ВАЖНО: нормализуем переводы строк к LF, иначе busybox sh может ругаться (set: illegal option -) на CRLF
            var scriptLf = script.Replace("\r\n", "\n").Replace("\r", "\n");
            var scriptB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptLf));
            var cmd = $"docker exec wireguard sh -lc 'echo {scriptB64} | base64 -d > /tmp/prov_no_psk.sh && chmod +x /tmp/prov_no_psk.sh && sh /tmp/prov_no_psk.sh'";
            var (exit, stdout, stderr) = await _ssh.RunCommandAsync(cmd, TimeSpan.FromMinutes(4), ct);
            if (exit != 0)
                throw new Exception($"Provision failed: {stderr}\n{stdout}");

            // Guard: убедимся, что PSK нигде не остался (wg showconf + grep файлов)
            var (_, g1, _) = await _ssh.RunCommandAsync("docker exec wireguard sh -lc 'wg showconf wg0 | grep -n ^PresharedKey || true'", TimeSpan.FromSeconds(15), ct);
            var (_, g2, _) = await _ssh.RunCommandAsync("docker exec wireguard sh -lc 'grep -n ^PresharedKey /config/peer*/peer*.conf 2>/dev/null || true'", TimeSpan.FromSeconds(15), ct);
            if (!string.IsNullOrWhiteSpace((g1 ?? string.Empty).Trim()) || !string.IsNullOrWhiteSpace((g2 ?? string.Empty).Trim()))
            {
                log("ERROR: PSK policy violated; cleaning any PresharedKey lines...");
                await _ssh.RunCommandAsync("docker exec wireguard sh -lc \"sed -i '/^PresharedKey/d' /config/wg0.conf 2>/dev/null; find /config -type f -name '*.conf' -exec sed -i '/^PresharedKey/d' {} + 2>/dev/null\"", TimeSpan.FromSeconds(20), ct);
                log("Fixed: removed PresharedKey lines from configs");
            }

            success($"Peers provisioned (N={n}), PSK=off");
        }

        public async Task<RunnerResult> RunAsync(SessionParams session, IProgress<string> progress, CancellationToken ct)
        {
            void LogLine(string line) => progress.Report(line);
            void SuccessMsg(string line) => progress.Report($"SuccessMsg: {line}");

            // wait-for-ssh helper moved to a private static method below

            void onCmd(string cmd) => progress.Report($"CMD: {cmd}");
            _ssh.CommandExecuting += onCmd;
            try
            {
                LogLine($"Connecting to {session.Host}:{session.Port} as {session.Login}...");
                // Connect: устанавливаем SSH-соединение с удалённым хостом (кратко)
                await _ssh.ConnectAsync(session.Host, session.Port, session.Login, session.Password, ct);

                // Preflight: первичная проверка и базовая настройка хоста (кратко)
                await PreflightAsync(session, LogLine, SuccessMsg, ct);
                // Docker: проверка/установка Docker (кратко)
                await EnsureDockerAsync(LogLine, SuccessMsg, ct);

                // Compose: проверка/установка Docker Compose (кратко)
                await EnsureComposeAsync(LogLine, SuccessMsg, ct);
                // Cleanup: удаление предыдущей установки WireGuard (кратко)
                await RemoveExistingAsync(session, LogLine, SuccessMsg, ct);

                // Prepare: каталоги + docker-compose.yml, возвращает public IP (кратко)
                var publicIp = await PrepareDirsAndComposeAsync(session, LogLine, ct);

                // Start & Verify: запуск контейнера и верификация, что реально работает (кратко)
                await StartContainerAndVerifyAsync(session, LogLine, SuccessMsg, ct);

                // New stage: deterministic peer provisioning without PSK
                await ProvisionPeersWithoutPskAsync(session, LogLine, SuccessMsg, ct);

                // Export: выгружаем артефакты и .conf на локальную машину (кратко)
                await ExportArtifactsAsync(session, LogLine, SuccessMsg, ct);

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
        private async Task PreflightAsync(SessionParams session, Action<string> log, Action<string> success, CancellationToken ct)
        {
            // Что делает: выполняет preflight-скрипт — проверяет систему, настраивает базовые параметры (порт, IPv6 и т.п.), собирает диагностику.
            // Зачем: гарантировать, что окружение совместимо и сеть подготовлена перед установкой и запуском контейнера.
            log("Preflight checks and host configuration...");
            var preflight = RemoteScripts.BuildPreflightScript(session.WgPort, session.DisableIPv6);
            var tmpScript = "/tmp/wg_preflight.sh";
            var fullScript = "#!/usr/bin/env bash\n" + preflight;
            Log.Debug("---- BEGIN preflight.sh ----\n{Script}\n---- END preflight.sh ----", fullScript);
            await _ssh.UploadTextAsync(tmpScript, fullScript, ct);
            var (exitP, stdoutP, stderrP) = await _ssh.RunCommandAsync($"bash {tmpScript}", TimeSpan.FromMinutes(10), ct);
            if (exitP != 0)
                throw new Exception($"preflight failed: {stderrP}\n{stdoutP}");

            try
            {
                var osLine = (stdoutP ?? string.Empty)
                    .Split('\n')
                    .Select(l => l.TrimEnd())
                    .FirstOrDefault(l => l.StartsWith("OS: ", StringComparison.OrdinalIgnoreCase));
                success(!string.IsNullOrWhiteSpace(osLine) ? $"Connected. {osLine}" : "Connected to host");
            }
            catch { success("Connected to host"); }

            log("Diagnostics output:");
            foreach (var l in (stdoutP ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) log(line); }
            foreach (var l in (stderrP ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) log(line); }
            log("Diagnostics complete.");
        }

        private async Task EnsureDockerAsync(Action<string> log, Action<string> success, CancellationToken ct)
        {
            // Что делает: проверяет наличие Docker; при отсутствии — устанавливает и запускает сервис.
            // Зачем: без Docker контейнер WireGuard не запустится; приводим систему к требуемому состоянию.
            log("Checking Docker...");
            var (chkDocker, _, _) = await _ssh.RunCommandAsync("command -v docker >/dev/null 2>&1", TimeSpan.FromSeconds(20), ct);
            if (chkDocker != 0)
            {
                log("Docker not found. Installing Docker (one attempt)...");
                await _ssh.RunCommandAsync("export DEBIAN_FRONTEND=noninteractive; apt-get update -y", TimeSpan.FromMinutes(5), ct);
                await _ssh.RunCommandAsync("apt-get install -y curl ca-certificates", TimeSpan.FromMinutes(5), ct);
                var (exitInstall, outInstall, errInstall) = await _ssh.RunCommandAsync("curl -fsSL https://get.docker.com | sh", TimeSpan.FromMinutes(10), ct);
                if (exitInstall != 0)
                {
                    log("WARN: Docker convenience script failed, trying apt-get install docker.io as fallback...");
                    var (exitAptDocker, outAptDocker, errAptDocker) = await _ssh.RunCommandAsync("apt-get install -y docker.io", TimeSpan.FromMinutes(10), ct);
                    if (exitAptDocker != 0)
                        throw new Exception($"Docker installation failed:\nscript: {errInstall}\n{outInstall}\naptdocker: {errAptDocker}\n{outAptDocker}");
                }
                await _ssh.RunCommandAsync("systemctl enable --now docker 2>/dev/null || service docker start 2>/dev/null || true", TimeSpan.FromMinutes(2), ct);
                var (chkDocker2, stdoutDv, stderrDv) = await _ssh.RunCommandAsync("docker --version", TimeSpan.FromSeconds(30), ct);
                if (chkDocker2 != 0)
                    throw new Exception($"Docker installation unsuccessful: {stderrDv}\n{stdoutDv}");
                log($"Docker installed: {stdoutDv.Trim()}");
                success($"Docker OK: {stdoutDv.Trim()}");
            }
            else
            {
                var (_, outDv, _) = await _ssh.RunCommandAsync("docker --version", TimeSpan.FromSeconds(30), ct);
                success($"Docker OK: {outDv?.Trim()}");
            }
        }

        private async Task EnsureUdpPortFreedAsync(int port, Action<string> log, CancellationToken ct)
        {
            // 0) Кто держит порт сейчас?
            var (_, beforeSs, _) = await _ssh.RunCommandAsync($"ss -H -ulpn | grep -E ':{port} ' || true", TimeSpan.FromSeconds(10), ct);
            if (!string.IsNullOrWhiteSpace(beforeSs))
                log($"cleanup: udp/{port} currently in use by:\n{beforeSs}");

            // 1) Остановить и отключить все wg-юниты, удалить интерфейсы wg*
            await _ssh.RunCommandAsync("systemctl stop 'wg-quick@*' 2>/dev/null || true", TimeSpan.FromSeconds(20), ct);
            await _ssh.RunCommandAsync("systemctl disable 'wg-quick@*' 2>/dev/null || true", TimeSpan.FromSeconds(20), ct);
            await _ssh.RunCommandAsync("ip -o link show | awk -F': ' '/^ *[0-9]+: wg[0-9]+(@|:|$)/ {print $2}' | xargs -r -I{} sh -c 'ip link del \"{}\" 2>/dev/null || true'", TimeSpan.FromSeconds(20), ct);

            // 2) Контейнеры: по имени и по публикуемому порту
            await _ssh.RunCommandAsync("docker ps -aq -f name=^wireguard$ | xargs -r docker rm -f", TimeSpan.FromSeconds(30), ct);
            await _ssh.RunCommandAsync($"docker ps -aq --filter 'publish={port}' | xargs -r docker rm -f", TimeSpan.FromSeconds(30), ct);

            // 3) Убить залипший docker-proxy (если он держит порт)
            var killProxyCmd =
                $"pids=$(ss -H -ulpn | awk '/:{port} / && /docker-proxy/ {{print $6}}' | sed -n 's/.*pid=\\([0-9]\\+\\).*/\\1/p' | sort -u); " +
                "[ -n \"$pids\" ] && kill -9 $pids 2>/dev/null || true";
            await _ssh.RunCommandAsync(killProxyCmd, TimeSpan.FromSeconds(10), ct);

            // 4) Удалить висячую сеть wireguard_default
            await _ssh.RunCommandAsync("docker network inspect wireguard_default >/dev/null 2>&1 && docker network rm wireguard_default || true",
                TimeSpan.FromSeconds(20), ct);

            // 5) Дождаться освобождения порта
            for (var i = 0; i < 10; i++)
            {
                var (_, outSs, _) = await _ssh.RunCommandAsync($"ss -H -ulpn | grep -E ':{port} ' || true", TimeSpan.FromSeconds(3), ct);
                if (string.IsNullOrWhiteSpace(outSs))
                {
                    log($"cleanup: udp/{port} is free");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            var (_, still, _) = await _ssh.RunCommandAsync($"ss -H -ulpn | grep -E ':{port} ' || true", TimeSpan.FromSeconds(3), ct);
            throw new Exception($"udp/{port} is still busy:\n{still}");
        }

        private async Task EnsureComposeAsync(Action<string> log, Action<string> success, CancellationToken ct)
        {
            // Что делает: проверяет наличие Docker Compose (plugin или v1); при отсутствии — устанавливает подходящий вариант.
            // Зачем: docker compose управляет сборкой/запуском сервиса; поддерживаем оба варианта для совместимости дистрибутивов.
            log("Checking Docker Compose...");
            var (chkCompose, _, _) = await _ssh.RunCommandAsync("docker compose version >/dev/null 2>&1", TimeSpan.FromSeconds(20), ct);
            var composeOk = chkCompose == 0;
            if (!composeOk)
            {
                var (chkComposeV1, _, _) = await _ssh.RunCommandAsync("command -v docker-compose >/dev/null 2>&1", TimeSpan.FromSeconds(20), ct);
                composeOk = chkComposeV1 == 0;
            }
            if (!composeOk)
            {
                log("Docker Compose not found. Installing Docker Compose (one attempt)...");
                await _ssh.RunCommandAsync("export DEBIAN_FRONTEND=noninteractive; apt-get update -y", TimeSpan.FromMinutes(5), ct);
                var (exitPlug, outPlug, errPlug) = await _ssh.RunCommandAsync("apt-get install -y docker-compose-plugin", TimeSpan.FromMinutes(10), ct);
                var (verPlugin, _, _) = await _ssh.RunCommandAsync("docker compose version >/dev/null 2>&1", TimeSpan.FromSeconds(20), ct);
                if (verPlugin != 0)
                {
                    var (exitV1, outV1, errV1) = await _ssh.RunCommandAsync("apt-get install -y docker-compose", TimeSpan.FromMinutes(10), ct);
                    var (verV1, stdoutV1, stderrV1) = await _ssh.RunCommandAsync("docker-compose --version", TimeSpan.FromSeconds(30), ct);
                    if (verV1 != 0)
                    {
                        log("WARN: apt docker-compose not available. Installing docker-compose v1 binary...");
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
                            throw new Exception($"Docker Compose installation unsuccessful:\nplugin: {errPlug}\n{outPlug}\nlegacy: {errV1}\n{outV1}\nlegacy-verify: {stderrV1}\n{stdoutV1}\nbinary: {errBin}\n{outBin}\nbinary-verify: {stderrV1b}\n{stdoutV1b}");
                        log($"Compose installed: {stdoutV1b.Trim()}");
                        success($"Docker Compose OK: {stdoutV1b.Trim()}");
                    }
                    else
                    {
                        log($"Compose installed: {stdoutV1.Trim()}");
                        success($"Docker Compose OK: {stdoutV1.Trim()}");
                    }
                }
                else
                {
                    var (_, stdoutC, stderrC) = await _ssh.RunCommandAsync("docker compose version", TimeSpan.FromSeconds(30), ct);
                    log($"Compose installed: {stdoutC.Trim()} {stderrC.Trim()}");
                    success($"Docker Compose OK: {stdoutC.Trim()} {stderrC.Trim()}");
                }
            }
            else
            {
                var (vPlugin, outPlugin, _) = await _ssh.RunCommandAsync("docker compose version", TimeSpan.FromSeconds(30), ct);
                if (vPlugin == 0)
                    success($"Docker Compose OK: {outPlugin.Trim()}");
                else
                {
                    var (v1, outV1only, _) = await _ssh.RunCommandAsync("docker-compose --version", TimeSpan.FromSeconds(30), ct);
                    if (v1 == 0) success($"Docker Compose OK: {outV1only.Trim()}");
                    else success("Docker Compose OK");
                }
            }
        }
        private async Task RemoveExistingAsync(SessionParams session, Action<string> log, Action<string> success, CancellationToken ct)
        {
            // Идемпотентная очистка перед установкой:
            // - стоп/disable всех wg-юнитов
            // - удаление интерфейсов wg*
            // - удаление контейнеров по имени и по публикуемому порту
            // - убийство залипшего docker-proxy
            // - удаление сети wireguard_default
            // - проверка, что udp/<WG_PORT> свободен
            log("Idempotent cleanup: stop wg, free port, remove containers, purge dir...");
            await EnsureUdpPortFreedAsync(session.WgPort, log, ct);

            // Найдём путь установленного WireGuard: проверяем /root/wireguard, /opt/wireguard, $HOME/wireguard
            var (_, homeOut, _) = await _ssh.RunCommandAsync("bash -lc 'echo -n ${HOME:-/root}'", TimeSpan.FromSeconds(10), ct);
            var home = string.IsNullOrWhiteSpace(homeOut) ? "/root" : homeOut.Trim();
            var candidates = new[] { "/root/wireguard", "/opt/wireguard", home + "/wireguard" };

            string? wgRoot = null;
            foreach (var p in candidates)
            {
                var (existsExit, _, _) = await _ssh.RunCommandAsync($"test -d '{p}'", TimeSpan.FromSeconds(5), ct);
                if (existsExit == 0) { wgRoot = p; break; }
            }

            if (string.IsNullOrWhiteSpace(wgRoot))
            {
                log("WireGuard not found");
                return; // выхода из метода достаточно; дальнейшая очистка не требуется
            }

            // Удаляем рабочую директорию
            await _ssh.RunCommandAsync($"rm -rf '{wgRoot}' 2>/dev/null || true", TimeSpan.FromMinutes(2), ct);

            // Верифицируем, что удалено
            var (exitDirOk, _, errDir) = await _ssh.RunCommandAsync($"test ! -d '{wgRoot}'", TimeSpan.FromSeconds(10), ct);
            if (exitDirOk != 0)
                throw new Exception($"Failed to remove {wgRoot} directory: {errDir}");

            success("WireGuard removed");
        }

        private async Task<string> PrepareDirsAndComposeAsync(SessionParams session, Action<string> log, CancellationToken ct)
        {
            // Что делает: готовит рабочую структуру на хосте и загружает корректный docker-compose.yml.
            //   1) Создаёт каталоги строго под /root/wireguard/config и проставляет права PUID/PGID=1000.
            //   2) Получает публичный IP и подставляет его в шаблон.
            //   3) Нормализует compose-файл: убирает устаревшее поле 'version:', заменяет ~/$HOME на абсолютный путь,
            //      гарантирует наличие маппинга /root/wireguard/config:/config и делает /lib/modules read-only.
            // Зачем: обеспечить единообразное и предсказуемое окружение (пути, права, тома), чтобы контейнер WireGuard
            //        стартовал как на эталоне (PRIMA), без ворнингов и без сюрпризов из-за разных путей/volume-монтажей.
            log("Preparing /root/wireguard and docker-compose.yml...");

            // 1) Готовим каталоги строго под /root/wireguard/config и права для PUID/PGID=1000
            var wgRoot = "/root/wireguard";
            var wgConfig = "/root/wireguard/config";
            await _ssh.EnsureDirectoryAsync(wgConfig, ct);
            await _ssh.RunCommandAsync($"chown -R 1000:1000 '{wgRoot}' 2>/dev/null || true", TimeSpan.FromSeconds(30), ct);

            // 2) Публичный IP подставим в шаблон
            var publicIp = await _ssh.GetPublicIpAsync(ct);

            // 3) Генерируем compose из шаблона, затем нормализуем:
            //   - убираем 'version:' (устаревшее поле — Docker ругается ворнингом)
            //   - заменяем любые пути вида ~/wireguard/config или ${HOME}/wireguard/config на /root/wireguard/config
            //   - на всякий случай приводим /lib/modules монтирование к read-only
            var composeRaw = ComposeTemplate.Generate(publicIp, session.WgPort, session.Peers) ?? string.Empty;

            // убираем строку version: ... (где бы она ни была)
            var compose = Regex.Replace(composeRaw, @"(?m)^\s*version\s*:\s*.*\r?\n", string.Empty);

            // нормализуем путь тома
            compose = compose.Replace("~/wireguard/config", wgConfig)
                             .Replace("${HOME}/wireguard/config", wgConfig);

            // делаем /lib/modules read-only (если не указан :ro)
            compose = Regex.Replace(
                compose,
                @"(?m)^(\s*-\s*/lib/modules:/lib/modules)(\s*)$",
                "$1:ro$2"
            );

            // подстрахуемся: если после правок нет явного маппинга /root/wireguard/config:/config — добавим
            if (!compose.Contains($"{wgConfig}:/config"))
            {
                // Вставим в конец секции volumes сервиса wireguard.
                compose = Regex.Replace(
                    compose,
                    @"(?s)(wireguard:\s*.*?\n\s*volumes:\s*\n)(\s*-\s*.*\n)*",
                    m => m.Value + $"      - {wgConfig}:/config\n"
                );
            }

            // 4) Заливаем итоговый compose на хост
            var composePath = $"{wgRoot}/docker-compose.yml";
            await _ssh.UploadTextAsync(composePath, compose, ct);

            log($"docker-compose.yml uploaded to {composePath}");

            // Optional pre-start PSK policy check/cleanup on host configs (if any legacy files exist)
            var (exPskGrep, outPskGrep, _) = await _ssh.RunCommandAsync($"grep -R -n '^PresharedKey' {wgConfig} 2>/dev/null || true", TimeSpan.FromSeconds(10), ct);
            if (!string.IsNullOrWhiteSpace(outPskGrep))
            {
                log("pre-start: found PresharedKey lines in host config, removing to enforce PSK=off:");
                foreach (var l in (outPskGrep ?? string.Empty).Split('\n')) { var line = l.TrimEnd(); if (!string.IsNullOrWhiteSpace(line)) log(line); }
                await _ssh.RunCommandAsync($"find {wgConfig} -type f -name '*.conf' -exec sed -i '/^PresharedKey/d' {{}} + 2>/dev/null", TimeSpan.FromSeconds(20), ct);
                log("pre-start: cleaned PresharedKey lines from host configs");
            }
            return publicIp;
        }


        private async Task StartContainerAndVerifyAsync(SessionParams session, Action<string> log, Action<string> success, CancellationToken ct)
        {
            // Что делает:
            //   1) docker compose pull/up с авто-выбором плагина/legacy.
            //   2) При фейле "up" печатает развёрнутую диагностику: кто держит порт, контейнеры с publish, статус wg-юнитов, логи контейнера.
            //   3) После успешного старта ждёт стабилизации SSH, сохраняет сетевой снапшот, проверяет, что контейнер реально Running.
            //   4) Дополнительно логирует наличие DNAT (порт 51820/udp) и "wg show" внутри контейнера.
            // Зачем:
            //   — чётко видеть причину "address already in use" и схлопывать её;
            //   — иметь пост-фактум след (routes/rules/iptables/nft) для сравнения с эталоном (PRIMA);
            //   — подтвердить, что Docker действительно прокинул порт и контейнер живёт.

            log("Starting WireGuard container...");

            var composeSelect = "COMPOSE=\"docker compose\"; docker compose version >/dev/null 2>&1 || COMPOSE=\"docker-compose\";";
            var composeFile = "/root/wireguard/docker-compose.yml";

            // 1) pull свежий образ (не критично, но полезно)
            var pullCmd = composeSelect + $" $COMPOSE -f {composeFile} pull";
            await _ssh.RunCommandAsync(pullCmd, TimeSpan.FromMinutes(15), ct);

            // Небольшой пред-чек: вдруг порт уже занят (например, внешним сервисом)
            var (_, preSs, _) = await _ssh.RunCommandAsync($"ss -H -ulpn | egrep ':{session.WgPort} ' || true", TimeSpan.FromSeconds(5), ct);
            if (!string.IsNullOrWhiteSpace(preSs))
                log($"WARN: before up, udp/{session.WgPort} is in use by:\n{preSs}");

            // Фоновый пост-старт снапшот сети (iptables/nft/routes)
            var postStartDiag = "nohup sh -c \"sleep 3; { " +
                                "date; echo '--- ip route'; ip -4 route; echo '--- ip rule'; ip rule; " +
                                "echo '--- ip addr'; ip addr; echo '--- iptables'; iptables -S 2>/dev/null; " +
                                "echo '--- iptables nat'; iptables -t nat -S 2>/dev/null; " +
                                "echo '--- nft ruleset (trimmed)'; nft list ruleset 2>/dev/null | sed -n '1,400p'; " +
                                "} > /tmp/wg_after_start.txt 2>&1\" >/dev/null 2>&1 &";
            await _ssh.RunCommandAsync(postStartDiag, TimeSpan.FromSeconds(10), ct);

            // Страховка: если SSH не вернётся, авто-снести контейнер
            var guardCmd = "nohup sh -c \"sleep 180; [ -f /tmp/wg_start_ok ] || docker rm -f wireguard\" >/dev/null 2>&1 &";
            await _ssh.RunCommandAsync(guardCmd, TimeSpan.FromSeconds(10), ct);

            // 2) up -d
            var upCmd = composeSelect + $" $COMPOSE -f {composeFile} up -d";
            var (exitUp, stdoutUp, stderrUp) = await _ssh.RunCommandAsync(upCmd, TimeSpan.FromMinutes(10), ct);
            if (exitUp != 0)
            {
                // Развёрнутая диагностика причины фейла (часто — занятый порт)
                var (_, outSs, _) = await _ssh.RunCommandAsync($"ss -H -ulpn | egrep ':{session.WgPort} ' || true", TimeSpan.FromSeconds(10), ct);
                var (_, outByPort, _) = await _ssh.RunCommandAsync($"docker ps --filter 'publish={session.WgPort}' --format '{{{{.ID}}}}  {{{{.Names}}}}  {{{{.Ports}}}}'", TimeSpan.FromSeconds(10), ct);
                var (_, wgUnit, _) = await _ssh.RunCommandAsync("systemctl is-active wg-quick@wg0 2>/dev/null || true", TimeSpan.FromSeconds(5), ct);
                var (_, psWire, _) = await _ssh.RunCommandAsync("docker ps -a --filter name=^wireguard$", TimeSpan.FromSeconds(10), ct);
                var (_, logsWire, _) = await _ssh.RunCommandAsync("docker logs --tail=200 wireguard 2>&1 || true", TimeSpan.FromSeconds(10), ct);

                throw new Exception(
                    "docker compose up failed.\n" +
                    $"STDERR:\n{stderrUp}\nSTDOUT:\n{stdoutUp}\n\n" +
                    $"== ss -ulpn :{session.WgPort} ==\n{outSs}\n" +
                    $"== docker ps publishing {session.WgPort} ==\n{outByPort}\n" +
                    $"== systemctl is-active wg-quick@wg0 ==\n{wgUnit}\n" +
                    $"== docker ps -a (wireguard) ==\n{psWire}\n" +
                    $"== docker logs wireguard (last 200) ==\n{logsWire}\n"
                );
            }

            // 3) Ждём, пока SSH стабилизируется
            log("Waiting for SSH to stabilize after starting container...");
            var sshBack = await WaitForSshPortAsync(session.Host, session.Port, TimeSpan.FromSeconds(150), ct, log);
            if (!sshBack)
            {
                log("SSH did not recover within 150s. The failsafe will remove the container shortly.");
                throw new Exception("SSH did not recover within the stabilization window after starting WireGuard.");
            }
            await _ssh.RunCommandAsync("touch /tmp/wg_start_ok", TimeSpan.FromSeconds(10), ct);

            // 4) Печатаем пост-старт снапшот
            var (_, outPostDiag, _) = await _ssh.RunCommandAsync("sed -n '1,200p' /tmp/wg_after_start.txt 2>/dev/null || true", TimeSpan.FromSeconds(30), ct);
            if (!string.IsNullOrWhiteSpace(outPostDiag))
            {
                log("diag: post-start network snapshot (first 200 lines):");
                foreach (var l in (outPostDiag ?? string.Empty).Split('\n'))
                {
                    var line = l.TrimEnd();
                    if (!string.IsNullOrWhiteSpace(line)) log(line);
                }
            }

            // 5) Убеждаемся, что контейнер реально Running
            log("Verifying container is running...");
            var inspectCmd = "docker inspect -f '{{.State.Running}}' wireguard 2>/dev/null";
            var runningOk = false;
            for (int attempt = 0; attempt < 3 && !runningOk; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
                var (exitInspect, outInspect, errInspect) = await _ssh.RunCommandAsync(inspectCmd, TimeSpan.FromSeconds(60), ct);
                var outTrim = (outInspect ?? string.Empty).Trim();
                var errTrim = (errInspect ?? string.Empty).Trim();
                log($"inspect attempt {attempt + 1}: exit={exitInspect}, out='{outTrim}', err='{errTrim}'");
                runningOk = exitInspect == 0 && string.Equals(outTrim, "true", StringComparison.OrdinalIgnoreCase);
            }
            if (!runningOk)
            {
                var (_, outPsErr, errPsErr) = await _ssh.RunCommandAsync("docker ps -a --filter name=^wireguard$", TimeSpan.FromSeconds(60), ct);
                throw new Exception($"WireGuard container is not running.\nps: {errPsErr}\n{outPsErr}");
            }

            // 6) Доп. проверки: DNAT/прокси и wg show внутри контейнера
            var (_, iptNat, _) = await _ssh.RunCommandAsync($"iptables -t nat -S 2>/dev/null | egrep '(:{session.WgPort}|DNAT|MASQUERADE)' || true", TimeSpan.FromSeconds(10), ct);
            if (!string.IsNullOrWhiteSpace(iptNat))
                log($"iptables nat (filtered):\n{iptNat}");

            var (_, nftNat, _) = await _ssh.RunCommandAsync($"nft list ruleset 2>/dev/null | egrep -n 'dport {session.WgPort}|dnat|masquerade' | sed -n '1,80p' || true", TimeSpan.FromSeconds(10), ct);
            if (!string.IsNullOrWhiteSpace(nftNat))
                log($"nftables (filtered):\n{nftNat}");

            var (_, outProxy, _) = await _ssh.RunCommandAsync($"ss -H -ulpn | egrep ':{session.WgPort} ' || true", TimeSpan.FromSeconds(5), ct);
            if (!string.IsNullOrWhiteSpace(outProxy))
                log($"listener(s) on udp/{session.WgPort}:\n{outProxy}");

            var (_, wgShow, _) = await _ssh.RunCommandAsync("docker exec wireguard sh -c 'wg show 2>/dev/null || true'", TimeSpan.FromSeconds(10), ct);
            if (!string.IsNullOrWhiteSpace(wgShow))
                log($"wg show (inside container):\n{wgShow}");

            // 7) Быстрая проверка политики PSK=off в текущем конфиге (без вывода приватных ключей)
            var (exConf, outConf, _) = await _ssh.RunCommandAsync(
                "docker exec wireguard sh -lc \"wg showconf wg0 | egrep -n 'PresharedKey|\\[Peer\\]|\\[Interface\\]' | sed -n '1,120p' || true\"",
                TimeSpan.FromSeconds(15), ct);
            if (!string.IsNullOrWhiteSpace(outConf))
            {
                log("wg showconf (filtered):");
                foreach (var l in (outConf ?? string.Empty).Split('\n'))
                {
                    var line = l.TrimEnd();
                    if (!string.IsNullOrWhiteSpace(line)) log(line);
                }
            }

            success("WireGuard started");
        }


        private async Task ExportArtifactsAsync(SessionParams session, Action<string> log, Action<string> success, CancellationToken ct)
        {
            // Что делает:
            //   1) Готовит локальную папку экспорта; скачивает рабочий каталог wgRoot (/root/wireguard или совместимый путь).
            //   2) Надёжно вытаскивает peer-конфиги из wgRoot/config (основной путь) с fallback на $HOME/wireguard/config (и /opt/wireguard/config для обратной совместимости).
            //   3) Собирает лёгкую диагностику: docker logs wireguard (последние 200 строк), docker inspect, docker ps.
            //   4) Печатает инвентарь *.conf/QR и путь экспорта.
            // Зачем:
            //   — единый источник правды для конфигов (wgRoot/config) + обратная совместимость,
            //   — быстрый пост-фактум след для дебага,
            //   — удобный список того, что именно экспортировалось.

            // Определим удалённый корень WireGuard (wgRoot): приоритет /root/wireguard, затем /opt/wireguard, затем $HOME/wireguard
            var (_, homeOut2, _) = await _ssh.RunCommandAsync("bash -lc 'echo -n ${HOME:-/root}'", TimeSpan.FromSeconds(10), ct);
            var home2 = string.IsNullOrWhiteSpace(homeOut2) ? "/root" : homeOut2.Trim();
            var rootCandidates = new[] { "/root/wireguard", "/opt/wireguard", home2 + "/wireguard" };
            string wgRoot = "/root/wireguard";
            foreach (var p in rootCandidates)
            {
                var (ex, _, _) = await _ssh.RunCommandAsync($"test -d '{p}'", TimeSpan.FromSeconds(5), ct);
                if (ex == 0) { wgRoot = p; break; }
            }

            log($"Exporting {wgRoot} to local folder: {session.ExportPath} ...");

            // 0) Подготовка локальной папки экспорта
            try
            {
                if (Directory.Exists(session.ExportPath))
                    Directory.Delete(session.ExportPath, recursive: true);
            }
            catch (Exception ex)
            {
                log($"WARN: failed to fully delete export folder: {ex.Message}");
            }
            try { Directory.CreateDirectory(session.ExportPath); } catch { }
            var localWireguardRoot = Path.Combine(session.ExportPath, "wireguard");
            try { Directory.CreateDirectory(localWireguardRoot); } catch { }

            // 1) Мини-диагностика на удалённой стороне (сохраним в {wgRoot}/_export_info)
            var prepInfoCmd = string.Join(" && ", new[]
            {
        $"mkdir -p {wgRoot}/_export_info",
        $"date > {wgRoot}/_export_info/host_time.txt",
        // последние 200 строк логов контейнера (если уже есть)
        $"docker logs --tail=200 wireguard > {wgRoot}/_export_info/docker_logs_wireguard.txt 2>&1 || true",
        // компактный inspect контейнера
        $"docker inspect wireguard > {wgRoot}/_export_info/docker_inspect_wireguard.json 2>/dev/null || true",
        // список контейнеров
        $"docker ps > {wgRoot}/_export_info/docker_ps.txt 2>&1 || true",
        // пометка политики: PSK=off
        $"echo 'PSK=off' > {wgRoot}/_export_info/policy.txt"
    });
            await _ssh.RunCommandAsync(prepInfoCmd, TimeSpan.FromSeconds(20), ct);

            // 2) Скачиваем весь каталог wgRoot (compose, конфиги, export_info)
            try
            {
                await _ssh.DownloadDirectoryAsync(wgRoot, localWireguardRoot, ct);
            }
            catch (Exception ex)
            {
                log($"WARN: failed to download {wgRoot}: {ex.Message}");
            }

            // 3) Peer-конфиги: основной путь и fallback
            //    Основной: wgRoot/config (единообразная схема).
            //    Fallback: $HOME/wireguard/config (на старых установках/шаблонах) и /opt/wireguard/config для обратной совместимости.
            var remoteCandidates = new System.Collections.Generic.List<string> { wgRoot + "/config", "/opt/wireguard/config" };

            // Вычислим $HOME на удалённом хосте и добавим fallback
            var (_, homeOut, _) = await _ssh.RunCommandAsync("bash -lc 'echo -n ${HOME:-/root}'", TimeSpan.FromSeconds(10), ct);
            var home = string.IsNullOrWhiteSpace(homeOut) ? "/root" : homeOut.Trim();
            remoteCandidates.Add($"{home}/wireguard/config");

            bool peersDownloaded = false;
            foreach (var remoteConfigRoot in remoteCandidates.Distinct())
            {
                // проверим, что папка существует и в ней есть хоть что-то
                var (existsExit, _, _) = await _ssh.RunCommandAsync(
                    $"test -d '{remoteConfigRoot}' && ls -1 '{remoteConfigRoot}' >/dev/null 2>&1",
                    TimeSpan.FromSeconds(5), ct);

                if (existsExit == 0)
                {
                    try
                    {
                        await _ssh.DownloadPeerConfigsAsync(remoteConfigRoot, localWireguardRoot, ct);
                        log($"Downloaded peer configs from: {remoteConfigRoot}");
                        peersDownloaded = true;
                        break; // основной путь найден — хватит
                    }
                    catch (Exception ex)
                    {
                        log($"WARN: failed to download peer configs from {remoteConfigRoot}: {ex.Message}");
                    }
                }
            }
            if (!peersDownloaded)
            {
                var candidatesListForLog = string.Join(", ", remoteCandidates.Distinct());
                log($"WARN: no peer configs found in any of: {candidatesListForLog}");
            }

            // 4) Инвентарь экспортированных артефактов (список *.conf и популярных QR/PNG)
            try
            {
                var confs = Directory.GetFiles(session.ExportPath, "*.conf", SearchOption.AllDirectories);
                var pngs = Directory.GetFiles(session.ExportPath, "*.png", SearchOption.AllDirectories);

                if (confs.Length > 0)
                {
                    log("Exported .conf files:");
                    foreach (var p in confs.OrderBy(x => x)) log(p);
                }
                if (pngs.Length > 0)
                {
                    log("Exported QR images:");
                    foreach (var p in pngs.OrderBy(x => x)) log(p);
                }
            }
            catch (Exception ex)
            {
                log($"WARN: inventory listing failed: {ex.Message}");
            }

            success($"WireGuard folder exported to: {session.ExportPath}");

            // 5) Выведем текущее docker ps (быстрый health-штрих)
            var (_, stdoutPs, stderrPs) = await _ssh.RunCommandAsync("docker ps", TimeSpan.FromSeconds(60), ct);
            foreach (var l in (stdoutPs ?? string.Empty).Split('\n'))
            {
                var line = l.TrimEnd();
                if (!string.IsNullOrWhiteSpace(line)) log(line);
            }
            foreach (var l in (stderrPs ?? string.Empty).Split('\n'))
            {
                var line = l.TrimEnd();
                if (!string.IsNullOrWhiteSpace(line)) log(line);
            }
        }


        private static async Task<bool> WaitForSshPortAsync(string host, int port, TimeSpan totalTimeout, CancellationToken token, Action<string> log)
        {
            // Что делает: активно ждёт доступности SSH-порта (TCP connect) с ретраями и логами.
            // Зачем: после сетевых изменений соединение может временно пропасть — дожидаемся восстановления доступа по SSH.
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
                        log($"SSH port reachable (attempt {attempt})");
                        return true;
                    }
                    else
                    {
                        log($"SSH port not reachable yet (attempt {attempt})");
                    }
                }
                catch (Exception)
                {
                    log($"SSH port connect exception (attempt {attempt})");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
            return false;
        }
    }
}
