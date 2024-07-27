using System.Net;

namespace JKP.CloudflareDynamicIPUpdate;

internal class IPAddressEqualityComparer : IEqualityComparer<IPAddress>
{
        
    public bool Equals(IPAddress? x, IPAddress? y)
    {
        if(x is null || y is null)
            return ReferenceEquals(x, y);
        return x.Equals(y);
    }

    public int GetHashCode(IPAddress obj) => obj.GetHashCode();
}