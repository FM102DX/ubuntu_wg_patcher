using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
        private readonly Runner _runner;
        private CancellationTokenSource? _cts;

        [ObservableProperty]
        private string host = string.Empty;
        [ObservableProperty]
        private int port = 22;
        [ObservableProperty]
        private string login = "root";
        [ObservableProperty]
        private string password = string.Empty;
        [ObservableProperty]
        private int wgPort = 51820;
        [ObservableProperty]
        private bool disableIPv6 = true;
        [ObservableProperty]
        private int peers = 3;
        [ObservableProperty]
        private string exportPath = string.Empty;

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

        public MainViewModel(SessionStorage storage, Runner runner)
        {
            _storage = storage;
            _runner = runner;
        }

        public async Task LoadLastSessionAsync()
        {
            var last = await _storage.LoadAsync();
            if (last == null) return;
            Host = last.Host;
            Port = last.Port;
            Login = last.Login;
            Password = last.Password;
            WgPort = last.WgPort;
            DisableIPv6 = last.DisableIPv6;
            Peers = last.Peers;
            ExportPath = last.ExportPath;
        }

        [RelayCommand]
        private void BrowseExportPath()
        {
            using var dlg = new FolderBrowserDialog();
            dlg.Description = "Select export folder";
            try
            {
                if (!string.IsNullOrWhiteSpace(ExportPath) && Directory.Exists(ExportPath))
                    dlg.SelectedPath = ExportPath;
                else
                    dlg.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            catch { }
            dlg.ShowNewFolderButton = true;
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                ExportPath = dlg.SelectedPath;
            }
        }

        [RelayCommand]
        private async Task StartAsync()
        {
            if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Login) || string.IsNullOrWhiteSpace(ExportPath))
                return;

            IsRunning = true;
            LogEntries.Clear();

            LogFilePath = LogService.CurrentLogFilePath ?? string.Empty;

            var session = new SessionParams
            {
                Host = Host,
                Port = Port,
                Login = Login,
                Password = Password,
                WgPort = WgPort,
                DisableIPv6 = DisableIPv6,
                Peers = Peers,
                ExportPath = ExportPath
            };

            await _storage.SaveAsync(session);

            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(s =>
            {
                AddLog(s);
            });

            try
            {
                var result = await _runner.RunAsync(session, progress, _cts.Token);
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
            var session = new SessionParams
            {
                Host = Host,
                Port = Port,
                Login = Login,
                Password = Password,
                WgPort = WgPort,
                DisableIPv6 = DisableIPv6,
                Peers = Peers,
                ExportPath = ExportPath
            };
            await _storage.SaveTempAsync(session);
            AddLog($"Saved session to {_storage.TempFilePath}", LogLevel.Success);
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
                var target = (!string.IsNullOrWhiteSpace(ExportPath) && Directory.Exists(ExportPath))
                    ? ExportPath
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
