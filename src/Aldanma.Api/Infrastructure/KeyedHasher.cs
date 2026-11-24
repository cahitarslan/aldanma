using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Infrastructure;

/// <summary>
/// HMAC-SHA256 with a server-side secret. Phone numbers have a small search space, so a plain hash
/// could be reversed by brute force; with a secret key a leaked database reveals nothing on its own.
/// </summary>
public sealed class KeyedHasher
{
    private readonly byte[] _key;

    /// <remarks>Startup validation (Program.cs) guarantees a real key outside Development and Testing.</remarks>
    public KeyedHasher(IOptions<AldanmaOptions> options)
    {
        var key = options.Value.HashKey;
        _key = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(key) ? "development-only-key" : key);
    }

    public byte[] Identifier(string kind, string value) => Hash("id:" + kind + ":" + value);

    /// <summary>
    /// Reporter key. Addresses are grouped by network (IPv4 /24, IPv6 /64) so one person switching addresses inside
    /// the same network, or a router full of devices, counts as one reporter. "Several independent reports" then
    /// means several networks, which is what makes a report count hard to inflate.
    /// </summary>
    public byte[] Reporter(string clientAddress) => Hash("reporter:" + NetworkOf(clientAddress));

    public static string NetworkOf(string clientAddress)
    {
        if (!IPAddress.TryParse(clientAddress, out var ip)) return clientAddress;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24"
            : Convert.ToHexString(bytes, 0, 8) + "/64";
    }

    private byte[] Hash(string input) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(input));
}
