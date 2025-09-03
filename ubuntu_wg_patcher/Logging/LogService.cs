using System;
using System.IO;
using Serilog;

namespace ubuntu_wg_patcher.Logging
{
    public static class LogService
    {
        public static string? CurrentLogFilePath { get; private set; }
        public static string LogsDirectory { get; private set; } = string.Empty;

        public static void StartNewLog()
        {
            var baseDir = AppContext.BaseDirectory;
            LogsDirectory = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(LogsDirectory);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            CurrentLogFilePath = Path.Combine(LogsDirectory, $"{stamp}.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(CurrentLogFilePath, rollingInterval: RollingInterval.Infinite, shared: true, flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();

            Log.Information("Log initialized at {Path}", CurrentLogFilePath);
        }
    }
}
