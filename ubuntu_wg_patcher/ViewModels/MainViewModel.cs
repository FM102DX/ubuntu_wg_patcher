using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using ubuntu_wg_patcher.Logging;
using ubuntu_wg_patcher.Models;
using ubuntu_wg_patcher.Services;

namespace ubuntu_wg_patcher.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly SessionStorage _storage;
        private readonly ISshClientService _ssh;
        private readonly IDiagnosticsService _diagnostics;
        private readonly Dictionary<ConfigurationType, IConfigRunner> _runners;
        private CancellationTokenSource? _cts;

        // Legacy single-config properties kept for backward compatibility with some commands; inputs now bind to SelectedConfig.*
        [ObservableProperty] private string host = string.Empty;
        [ObservableProperty] private int port = 22;
        [ObservableProperty] private string login = "root";
        [ObservableProperty] private string password = string.Empty;
        [ObservableProperty] private int wgPort = 51820;
        [ObservableProperty] private bool disableIPv6 = true;
        [ObservableProperty] private int peers = 3;
        [ObservableProperty] private string exportPath = string.Empty;

        [ObservableProperty]
        private int stepIndex = 0; // 0 input, 1 progress, 2 result
        [ObservableProperty]
        private bool isRunning = false;

        [ObservableProperty]
        private string publicIp = string.Empty;
        [ObservableProperty]
        private string geoJson = string.Empty;
        [ObservableProperty]
        private string logFilePath = string.Empty;

        public ObservableCollection<LogEntry> LogEntries { get; } = new();

        public ObservableCollection<SessionParams> Configs { get; } = new();

        [ObservableProperty]
        private SessionParams? selectedConfig;

        public Array ConfigTypes => Enum.GetValues(typeof(ConfigurationType));

        public MainViewModel(SessionStorage storage, ISshClientService ssh, IConfigRunner wireGuardRunner, IConfigRunner vlessRunner, IDiagnosticsService diagnostics)
        {
            _storage = storage;
            _ssh = ssh;
            _diagnostics = diagnostics;
            _runners = new()
            {
                [ConfigurationType.WireGuard] = wireGuardRunner,
                [ConfigurationType.VLESS] = vlessRunner
            };
        }

        public async Task LoadLastSessionAsync()
        {
            var list = await _storage.LoadListAsync();
            Configs.Clear();
            foreach (var c in list) Configs.Add(c);
            if (Configs.Count == 0)
            {
                var def = new SessionParams { ConfigName = "Config 1", ConfigType = ConfigurationType.WireGuard };
                Configs.Add(def);
            }
            SelectedConfig = Configs.FirstOrDefault();
        }

        [RelayCommand]
        private void BrowseExportPath()
        {
            using var dlg = new FolderBrowserDialog();
            dlg.Description = "Select export folder";
            try
            {
                var path = SelectedConfig?.ExportPath ?? ExportPath;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    dlg.SelectedPath = path;
                else
                    dlg.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            catch { }
            dlg.ShowNewFolderButton = true;
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                if (SelectedConfig != null)
                    SelectedConfig.ExportPath = dlg.SelectedPath;
                else
                    ExportPath = dlg.SelectedPath;
            }
        }

        [RelayCommand]
        private async Task StartAsync()
        {
            var cfg = SelectedConfig;
            if (cfg == null) return;
            if (string.IsNullOrWhiteSpace(cfg.Host) || string.IsNullOrWhiteSpace(cfg.Login) || string.IsNullOrWhiteSpace(cfg.ExportPath))
                return;

            // Two-step confirmation (destructive operation)
            var name = string.IsNullOrWhiteSpace(cfg.ConfigName) ? cfg.Host : cfg.ConfigName;
            var msg1 = $"Это полностью перенастроит сервер {name}. Продолжить?";
            var res1 = MessageBox.Show(msg1, "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (res1 != DialogResult.Yes)
            {
                AddLog("Cancelled: user aborted at first confirmation.");
                return;
            }
            var msg2 = $"Это ПОЛНОСТЬЮ ПЕРЕЗАПИШЕТ КОНФИГУРАЦИЮ СЕРВЕРА {name}. Продолжить?";
            var res2 = MessageBox.Show(msg2, "ВНИМАНИЕ", MessageBoxButtons.YesNo, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2);
            if (res2 != DialogResult.Yes)
            {
                AddLog("Cancelled: user aborted at second confirmation.");
                return;
            }

            IsRunning = true;
            LogEntries.Clear();

            LogFilePath = LogService.CurrentLogFilePath ?? string.Empty;

            var session = cfg.Clone();
            // Persist all current configs before run
            await _storage.SaveListAsync(Configs);

            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(s =>
            {
                AddLog(s);
            });

            try
            {
                if (!_runners.TryGetValue(session.ConfigType, out var runner))
                    throw new InvalidOperationException($"No runner registered for type {session.ConfigType}");
                var result = await runner.RunAsync(session, progress, _cts.Token);
                PublicIp = result.PublicIp;
                GeoJson = result.GeoJson;
                LogFilePath = result.LogFilePath;
                AddLog("SUCCESS: Finished", LogLevel.Success);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Run failed");
                AddLog($"ERROR: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                IsRunning = false;
            }
        }

        [RelayCommand]
        private async Task SaveTempAsync()
        {
            await _storage.SaveTempListAsync(Configs);
            AddLog($"Saved {Configs.Count} configurations to {_storage.TempFilePath}", LogLevel.Success);
        }

        [RelayCommand]
        private void CreateConfig()
        {
            var idx = Configs.Count + 1;
            var cfg = new SessionParams { ConfigName = $"Config {idx}", ConfigType = ConfigurationType.WireGuard };
            Configs.Add(cfg);
            SelectedConfig = cfg;
        }

        [RelayCommand]
        private void DeleteConfig()
        {
            if (SelectedConfig == null) return;
            var toRemove = SelectedConfig;
            var index = Configs.IndexOf(toRemove);
            if (index >= 0) Configs.RemoveAt(index);
            if (Configs.Count == 0)
            {
                var def = new SessionParams { ConfigName = "Config 1", ConfigType = ConfigurationType.WireGuard };
                Configs.Add(def);
            }
            SelectedConfig = Configs.FirstOrDefault();
        }

        [RelayCommand]
        private async Task SaveAllAsync()
        {
            await _storage.SaveListAsync(Configs);
            AddLog($"Saved {Configs.Count} configurations to {_storage.FilePath}", LogLevel.Success);
        }

        [RelayCommand]
        private async Task SmokeAsync()
        {
            var cfg = SelectedConfig;
            if (cfg == null) return;
            try
            {
                AddLog($"Smoke: connecting to {cfg.Host}:{cfg.Port} as {cfg.Login}...");
                await _ssh.ConnectAsync(cfg.Host, cfg.Port, cfg.Login, cfg.Password, CancellationToken.None);
                var script = string.Join(" ", new[]
                {
                    "OS_NAME=$( (lsb_release -ds 2>/dev/null) ||",
                    "(awk -F= '$1==\"PRETTY_NAME\"{print $2}' /etc/os-release 2>/dev/null | tr -d '\"') ||",
                    "uname -a ) ; echo \"$OS_NAME\""
                });
                var (exit, stdout, stderr) = await _ssh.RunCommandAsync(script, TimeSpan.FromSeconds(30), CancellationToken.None);
                if (exit == 0)
                {
                    var os = (stdout ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(os)) os = "<unknown OS>";
                    AddLog($"Smoke OK: {os}", LogLevel.Success);
                }
                else
                {
                    AddLog($"FATAL: Smoke failed (exit {exit}). {stderr}\n{stdout}", LogLevel.Fatal);
                }
            }
            catch (Exception ex)
            {
                AddLog($"FATAL: Smoke exception: {ex.Message}", LogLevel.Fatal);
            }
        }

        [RelayCommand]
        private async Task ServerInfoAsync()
        {
            var cfg = SelectedConfig;
            if (cfg == null) return;
            if (string.IsNullOrWhiteSpace(cfg.Host) || string.IsNullOrWhiteSpace(cfg.Login))
            {
                AddLog("Error: Host or Login is empty", LogLevel.Error);
                return;
            }
            if (string.IsNullOrWhiteSpace(cfg.ExportPath))
            {
                AddLog("Error: Export folder is empty", LogLevel.Error);
                return;
            }

            var progress = new Progress<string>(s => AddLog(s));
            var result = await _diagnostics.RunServerInfoAsync(cfg, progress, CancellationToken.None);
            if (result == null)
            {
                // Service already logged details and Fatal messages; ensure at least one fatal marker
                AddLog("Fatal: ServerInfo did not complete successfully", LogLevel.Fatal);
            }
        }

        [RelayCommand]
        private void ClearLogs()
        {
            LogEntries.Clear();
        }

        private void AddLog(string message, LogLevel? level = null)
        {
            var lvl = level ?? DetectLevel(message);
            LogEntries.Insert(0, new LogEntry
            {
                Message = message,
                Level = lvl,
                Timestamp = DateTime.Now
            });

            // Mirror GUI log lines to file via Serilog
            try
            {
                switch (lvl)
                {
                    case LogLevel.Error:
                        Log.Error("{Message}", message);
                        break;
                    case LogLevel.Success:
                    case LogLevel.Command:
                    case LogLevel.Info:
                    default:
                        Log.Information("{Message}", message);
                        break;
                }
            }
            catch { }
        }

        private static LogLevel DetectLevel(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return LogLevel.Info;
            var t = message.Trim();
            var tl = t.ToLowerInvariant();
            if (tl.StartsWith("successmsg:"))
                return LogLevel.SuccessMsg;
            if (tl.StartsWith("cmd:"))
                return LogLevel.Command;
            if (tl.StartsWith("fatal") || tl.Contains(" fatal ") || tl.Contains(" panic "))
                return LogLevel.Fatal;
            if (tl.StartsWith("error") || tl.Contains(" error") || tl.Contains(" failed") || tl.Contains(" fail"))
                return LogLevel.Error;
            if (tl.StartsWith("ok") || tl.StartsWith("success") || tl.Contains(" success") || tl.Contains(" done") || tl.Contains(" completed"))
                return LogLevel.Success;
            return LogLevel.Info;
        }

        private static string SanitizeFilePart(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "config";
            var s = input;
            foreach (var ch in Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');
            return s;
        }

        [RelayCommand]
        private async Task TakeComposeAsync()
        {
            var cfg = SelectedConfig;
            if (cfg == null) return;
            if (string.IsNullOrWhiteSpace(cfg.ExportPath))
            {
                AddLog("Error: Export folder is empty", LogLevel.Error);
                return;
            }
            try
            {
                AddLog($"Compose: connecting to {cfg.Host}:{cfg.Port} as {cfg.Login}...");
                await _ssh.ConnectAsync(cfg.Host, cfg.Port, cfg.Login, cfg.Password, CancellationToken.None);

                var detectCmd = string.Join(" ", new[]
                {
                    "if [ -f /opt/wireguard/docker-compose.yml ]; then echo /opt/wireguard/docker-compose.yml;",
                    "elif [ -f /opt/wireguard/docker-compose.yaml ]; then echo /opt/wireguard/docker-compose.yaml;",
                    "else echo NOT_FOUND; fi"
                });
                var (exit, stdout, stderr) = await _ssh.RunCommandAsync(detectCmd, TimeSpan.FromSeconds(20), CancellationToken.None);
                var remote = (stdout ?? string.Empty).Trim();
                if (exit != 0 || string.IsNullOrWhiteSpace(remote) || remote.Equals("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("Error: docker-compose file not found on server (/opt/wireguard).", LogLevel.Error);
                    return;
                }

                Directory.CreateDirectory(cfg.ExportPath);
                var safe = SanitizeFilePart(cfg.ConfigName);
                var local = Path.Combine(cfg.ExportPath, $"docker-compose_{safe}_{Guid.NewGuid():N}.yml");
                await _ssh.DownloadFileAsync(remote, local, CancellationToken.None);
                AddLog($"SuccessMsg: docker-compose saved to: {local}", LogLevel.SuccessMsg);
            }
            catch (Exception ex)
            {
                AddLog($"Fatal: Take compose failed: {ex.Message}", LogLevel.Fatal);
            }
        }

        [RelayCommand]
        private void OpenExportFolder()
        {
            try
            {
                var path = SelectedConfig?.ExportPath ?? ExportPath;
                var target = (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    ? path
                    : "C:\\";
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        [RelayCommand]
        private void OpenLogsFolder()
        {
            try
            {
                var dir = LogService.LogsDirectory;
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
            catch { }
        }
    }
}
