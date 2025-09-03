using System;
using System.Text.Json.Serialization;

namespace ubuntu_wg_patcher.Models
{
    public class SessionParams
    {
        [JsonPropertyName("Host")]
        public string Host { get; set; } = string.Empty;

        [JsonPropertyName("Port")]
        public int Port { get; set; } = 22;

        [JsonPropertyName("Login")]
        public string Login { get; set; } = "root";

        [JsonPropertyName("Password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("WgPort")]
        public int WgPort { get; set; } = 51820;

        [JsonPropertyName("DisableIPv6")]
        public bool DisableIPv6 { get; set; } = true;

        [JsonPropertyName("Peers")]
        public int Peers { get; set; } = 3;

        [JsonPropertyName("ExportPath")]
        public string ExportPath { get; set; } = string.Empty;

        public SessionParams Clone()
        {
            return (SessionParams)MemberwiseClone();
        }
    }
}
