using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Infrastructure;

/// <summary>
/// Resolves the client IP, used only for rate limits and as a keyed hash. A header such as CF-Connecting-IP is
/// trusted only when configured, because anyone can send it to an API that is reachable without the proxy.
/// </summary>
public sealed class ClientAddress(IOptions<AldanmaOptions> options)
{
    private readonly string _header = options.Value.ClientIpHeader;

    public string Of(HttpContext ctx)
    {
        if (_header.Length > 0 && ctx.Request.Headers.TryGetValue(_header, out var value) && !string.IsNullOrWhiteSpace(value))
            // For a list header (X-Forwarded-For) the entry appended by the nearest proxy, the last one, is the trustworthy one.
            return value.ToString().Split(',')[^1].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>The rate-limit bucket of a request: see <see cref="LimitKeyOf(string)"/>.</summary>
    public string LimitKey(HttpContext ctx) => LimitKeyOf(Of(ctx));

    /// <summary>
    /// IPv4 per address: Turkish mobile carriers put many customers behind one CGNAT range, so a /24 would throttle
    /// strangers together. IPv6 per /64: one subscriber usually gets a whole /64 and can rotate addresses inside it freely.
    /// </summary>
    public static string LimitKeyOf(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return address;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? KeyedHasher.NetworkOf(ip.ToString()) : ip.ToString();
    }
}
