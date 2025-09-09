using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ubuntu_wg_patcher.Models
{
    public partial class SessionParams : ObservableObject
    {
        [ObservableProperty]
        [JsonPropertyName("ConfigName")]
        private string configName = "Default";

        [ObservableProperty]
        [JsonPropertyName("ConfigType")]
        private ConfigurationType configType = ConfigurationType.WireGuard;

        [ObservableProperty]
        [JsonPropertyName("Host")]
        private string host = string.Empty;

        [ObservableProperty]
        [JsonPropertyName("Port")]
        private int port = 22;

        [ObservableProperty]
        [JsonPropertyName("Login")]
        private string login = "root";

        [ObservableProperty]
        [JsonPropertyName("Password")]
        private string password = string.Empty;

        [ObservableProperty]
        [JsonPropertyName("WgPort")]
        private int wgPort = 51820;

        [ObservableProperty]
        [JsonPropertyName("DisableIPv6")]
        private bool disableIPv6 = true;

        [ObservableProperty]
        [JsonPropertyName("Peers")]
        private int peers = 3;

        [ObservableProperty]
        [JsonPropertyName("ExportPath")]
        private string exportPath = string.Empty;

        public SessionParams Clone()
        {
            return new SessionParams
            {
                ConfigName = this.ConfigName,
                ConfigType = this.ConfigType,
                Host = this.Host,
                Port = this.Port,
                Login = this.Login,
                Password = this.Password,
                WgPort = this.WgPort,
                DisableIPv6 = this.DisableIPv6,
                Peers = this.Peers,
                ExportPath = this.ExportPath
            };
        }
    }
}
