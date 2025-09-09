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

        public MainViewModel(SessionStorage storage, IConfigRunner wireGuardRunner, IConfigRunner vlessRunner)
        {
            _storage = storage;
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
            if (tl.StartsWith("error") || tl.Contains(" error") || tl.Contains(" failed") || tl.Contains(" fail"))
                return LogLevel.Error;
            if (tl.StartsWith("ok") || tl.StartsWith("success") || tl.Contains(" success") || tl.Contains(" done") || tl.Contains(" completed"))
                return LogLevel.Success;
            return LogLevel.Info;
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
