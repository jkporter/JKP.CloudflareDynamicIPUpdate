using System.Net;

namespace JKP.CloudflareDynamicIPUpdate;

internal class IPAddressEqualityComparer : IEqualityComparer<IPAddress>
{

    public bool Equals(IPAddress? x, IPAddress? y) => x?.Equals(y) ?? ReferenceEquals(x, y);

    public int GetHashCode(IPAddress obj) => obj.GetHashCode();
}