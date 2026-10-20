using System.Net;
using System.Net.Sockets;

namespace Dockmaster.Server.Services;

/// <summary>Hands out addresses of the mesh subnet: the lowest free host address, never network or broadcast.</summary>
public static class SubnetAllocator
{
    public static string? Validate(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0 || !IPAddress.TryParse(cidr[..slash], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(cidr[(slash + 1)..], out var bits))
            return "subnet must look like 10.77.0.0/16";
        if (bits is < 8 or > 24) return "subnet prefix must be between /8 and /24";
        if (!MeshPlanner.IsPrivate(ip.ToString())) return "subnet must be a private range (10.x, 172.16-31.x, 192.168.x)";
        var raw = ToUint(ip.ToString());
        if (raw != (raw & Mask(bits))) return $"subnet address has host bits set — did you mean {Format(raw & Mask(bits))}/{bits}?";
        return null;
    }

    public static string Next(string cidr, IEnumerable<string> used)
    {
        var (net, bits) = Parse(cidr);
        var taken = used.Select(ToUint).ToHashSet();
        var first = net + 1;
        var last = (net | ~Mask(bits)) - 1; // broadcast - 1
        for (var ip = first; ip <= last; ip++)
            if (!taken.Contains(ip)) return Format(ip);
        throw new InvalidOperationException($"the mesh subnet {cidr} has no free address left");
    }

    /// <summary>The prefix length of a subnet: 16 for "10.77.0.0/16".</summary>
    public static int PrefixLength(string cidr) => Parse(cidr).bits;

    public static bool Contains(string cidr, string ip)
    {
        var (net, bits) = Parse(cidr);
        return (ToUint(ip) & Mask(bits)) == net;
    }

    private static (uint net, int bits) Parse(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var bits = int.Parse(cidr[(slash + 1)..]);
        return (ToUint(cidr[..slash]) & Mask(bits), bits);
    }

    private static uint ToUint(string ip)
    {
        var b = IPAddress.Parse(ip.Trim()).GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static uint Mask(int bits) => bits == 0 ? 0 : uint.MaxValue << (32 - bits);
    private static string Format(uint ip) => $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";
}
