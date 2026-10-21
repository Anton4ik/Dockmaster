using System.Net;
using System.Net.Sockets;

namespace Dockmaster.Server.Services;

/// <summary>
/// The connection under the "registry" HTTP client. A request marked <see cref="PublicOnly"/> (a token service a
/// registry named, which the server did not choose) only connects to public addresses, checked after DNS so a name that
/// points inside cannot get around it; redirects keep the mark.
/// </summary>
public static class RegistryHttp
{
    public static readonly HttpRequestOptionsKey<bool> PublicOnly = new("dockmaster.public-only");

    public static SocketsHttpHandler Handler() => new() { ConnectCallback = ConnectAsync };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct);
        if (ctx.InitialRequestMessage.Options.TryGetValue(PublicOnly, out var publicOnly) && publicOnly)
        {
            addresses = addresses.Where(IsPublic).ToArray();
            if (addresses.Length == 0) throw new HttpRequestException($"{host} is not on a public address: refused as the registry's token service");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>False for loopback, private, link-local, shared (100.64/10), multicast and reserved addresses.</summary>
    public static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 or >= 224 => false,
                100 => b[1] is < 64 or > 127,
                169 => b[1] != 254,
                172 => b[1] is < 16 or > 31,
                192 => !(b[1] == 168 || b[1] == 0 && b[2] is 0 or 2),
                198 => b[1] is not (18 or 19),
                _ => true,
            };
        }
        if (a.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (a.Equals(IPAddress.IPv6None) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.IsIPv6UniqueLocal) return false;
        var v6 = a.GetAddressBytes();
        // ::/96 (old IPv4-compatible), 64:ff9b::/96 (NAT64 may reach the inside), 2001:db8::/32 (documentation)
        if (v6.Take(12).All(x => x == 0)) return false;
        if (v6[0] == 0 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b) return false;
        return !(v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8);
    }
}
