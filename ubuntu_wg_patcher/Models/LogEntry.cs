using System;

namespace ubuntu_wg_patcher.Models
{
    public enum LogLevel
    {
        Info,
        Command,
        Success,
        SuccessMsg,
        Error
    }

    public class LogEntry
    {
        public string Message { get; set; } = string.Empty;
        public LogLevel Level { get; set; } = LogLevel.Info;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
