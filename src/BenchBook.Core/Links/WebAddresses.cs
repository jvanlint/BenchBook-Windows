using System.Net;
using System.Net.Sockets;

namespace BenchBook.Core.Links;

/// <summary>
/// Which web addresses link previews may fetch (behaviour.md, "Webpages";
/// WebLoader.swift). A page can't make BenchBook fetch something on the local
/// network, such as a router: redirects, preview images, icons and posters must
/// be on the public internet. Names aren't looked up; they're judged as written.
/// </summary>
public static class WebAddresses
{
    private static readonly string[] LocalSuffixes = ["localhost", "local", "internal", "lan", "home.arpa", "localdomain"];

    /// <summary>http or https, with a host.</summary>
    public static bool IsWeb(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Length > 0;

    /// <summary>An http or https address on the public internet: not this PC, not the local network, and not a name that only works there.</summary>
    public static bool IsPublic(Uri url)
    {
        if (!IsWeb(url)) return false;
        var host = url.Host.ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host.EndsWith('.')) host = host[..^1];

        // An address in any form .NET reads: "10.0.0.1", "::1", and shorthand such as "127.1" or "2130706433".
        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(host, out var address))
            return IsPublic(address);

        var labels = host.Split('.');
        // A single name ("router") or an empty label only resolves locally.
        if (labels.Length < 2 || labels.Any(l => l.Length == 0)) return false;
        // No real top-level domain is a number, so this is an address in another form ("0x7f.0.0.1").
        var last = labels[^1];
        if (last.All(char.IsAsciiDigit) || last.StartsWith("0x", StringComparison.Ordinal)) return false;
        return !LocalSuffixes.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return IsPublicV4((uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.All(x => x == 0)) return false;                                   // ::
        if (bytes[..15].All(x => x == 0) && bytes[15] == 1) return false;           // ::1
        if ((bytes[0] & 0xFE) == 0xFC) return false;                                 // unique local
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return false;            // link-local
        if (bytes[0] == 0xFF) return false;                                          // multicast
        // An IPv4 address written as IPv6 (::ffff:10.0.0.1).
        if (bytes[..10].All(x => x == 0) && bytes[10] == 0xFF && bytes[11] == 0xFF)
            return IsPublicV4((uint)(bytes[12] << 24 | bytes[13] << 16 | bytes[14] << 8 | bytes[15]));
        return true;
    }

    private static bool IsPublicV4(uint address)
    {
        var a = address >> 24;
        var b = (address >> 16) & 0xFF;
        return a switch
        {
            0 or 10 or 127 => false,                         // this network, private, loopback
            100 when b is >= 64 and <= 127 => false,          // carrier-grade NAT
            169 when b == 254 => false,                       // link-local
            172 when b is >= 16 and <= 31 => false,
            192 when b == 168 => false,
            192 when b == 0 && ((address >> 8) & 0xFF) == 0 => false,
            198 when b is 18 or 19 => false,                  // benchmarking
            >= 224 => false,                                  // multicast and reserved
            _ => true,
        };
    }
}
