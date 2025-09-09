using System.Text.Json.Serialization;

namespace ubuntu_wg_patcher.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ConfigurationType
    {
        WireGuard,
        VLESS
    }
}
