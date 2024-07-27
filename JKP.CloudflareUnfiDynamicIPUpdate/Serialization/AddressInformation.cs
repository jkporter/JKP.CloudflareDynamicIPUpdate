using System.Net;
using System.Text.Json.Serialization;

namespace JKP.CloudflareDynamicIPUpdate.Serialization;

public class AddressInformation
{
    [JsonConverter(typeof(IPAddressConverter))]
    public IPAddress Local { get; set; }
    [JsonPropertyName("prefixlen")]
    public int PrefixLength { get; set; }
    public string Scope { get; set; }
    public bool Dynamic { get; set; }
    public string Label { get; set; }
    [JsonConverter(typeof(TimeSpanConvertor))]
    public TimeSpan ValidLifeTime { get; set; }
    [JsonConverter(typeof(TimeSpanConvertor))]
    public TimeSpan PreferredLifeTime { get; set; }
    [JsonPropertyName("stable-privacy")]
    public bool? StablePrivacy { get; set; }
}