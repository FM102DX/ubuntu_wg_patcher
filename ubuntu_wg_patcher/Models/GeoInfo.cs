using System.Text.Json.Serialization;

namespace ubuntu_wg_patcher.Models
{
    public class GeoInfo
    {
        [JsonPropertyName("ip")] public string Ip { get; set; } = string.Empty;
        [JsonPropertyName("city")] public string City { get; set; } = string.Empty;
        [JsonPropertyName("region")] public string Region { get; set; } = string.Empty;
        [JsonPropertyName("country")] public string Country { get; set; } = string.Empty;
        [JsonPropertyName("org")] public string Org { get; set; } = string.Empty;
    }
}
