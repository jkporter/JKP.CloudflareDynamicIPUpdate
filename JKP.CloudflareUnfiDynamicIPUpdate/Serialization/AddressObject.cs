using System.Net.NetworkInformation;
using System.Text.Json.Serialization;

namespace JKP.CloudflareDynamicIPUpdate.Serialization;

public class AddressObject
{
    [JsonPropertyName("ifindex")]
    public int InterfaceIndex { get; set; }
    public string? Link { get; set; }
    [JsonPropertyName("ifname")]
    public string InterfaceName { get; set; }
    public string[] Flags { get; set; }
    public int Mtu { get; set; }
    [JsonPropertyName("qdisc")]
    public string QueueingDiscipline { get; set; }
    public string? Master { get; set; }
    [JsonPropertyName("operstate")]
    public string OperationState { get; set; }
    public string Group { get; set; }
    [JsonPropertyName("txqlen")]
    public int TransmitQueueLength { get; set; }
    public string LinkType { get; set; }
    [JsonConverter(typeof(PhysicalAddressConverter))]
    public PhysicalAddress Address { get; set; }
    [JsonConverter(typeof(PhysicalAddressConverter))]
    public PhysicalAddress Broadcast { get; set; }
    [JsonPropertyName("addr_info")]
    public AddressInformation[] AddressInformation { get; set; }
}