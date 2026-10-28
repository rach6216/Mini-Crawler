using System.Net;
using System.Net.Sockets;

namespace Crawler.Core.Domain;

/// <summary>
/// SSRF guard shared by the API (syntactic checks on submitted URLs) and the worker (checks on every
/// resolved address before a socket is opened). Trusted hosts are exempt so that local fixture
/// servers can be crawled in tests and in the Docker demo without weakening the default policy.
/// </summary>
public static class UrlSafety
{
    public static bool IsTrustedHost(string host, IEnumerable<string> trustedHosts) =>
        trustedHosts.Any(trusted => string.Equals(trusted, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rejects hosts that are obviously unsafe without DNS: loopback names and non-public IP literals.
    /// DNS-based validation happens in the fetcher, where the resolved addresses are known.
    /// </summary>
    public static bool IsHostAllowedWithoutDns(string host, IEnumerable<string> trustedHosts)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (IsTrustedHost(host, trustedHosts))
        {
            return true;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var literal = host.Trim('[', ']');
        return !IPAddress.TryParse(literal, out var address) || IsPubliclyRoutable(address);
    }

    public static bool IsPubliclyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.None))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            _ => false,
        };
    }

    private static bool IsPublicIPv4(byte[] b)
    {
        var blocked =
            b[0] == 0                                   // "this" network
            || b[0] == 10                               // private
            || b[0] == 127                              // loopback
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // carrier-grade NAT
            || (b[0] == 169 && b[1] == 254)             // link-local, cloud metadata
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // private
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)  // IETF protocol assignments
            || (b[0] == 192 && b[1] == 0 && b[2] == 2)  // documentation
            || (b[0] == 192 && b[1] == 168)             // private
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // benchmarking
            || (b[0] == 198 && b[1] == 51 && b[2] == 100) // documentation
            || (b[0] == 203 && b[1] == 0 && b[2] == 113)  // documentation
            || b[0] >= 224;                             // multicast and reserved

        return !blocked;
    }

    /// <summary>
    /// Allow-list approach: only the global unicast block 2000::/3 can be public, minus the special-purpose
    /// ranges inside it. Everything else (loopback, link-local, ULA, multicast, NAT64, IPv4-compatible, ...)
    /// is rejected without needing to be enumerated.
    /// </summary>
    private static bool IsPublicIPv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || address.IsIPv6UniqueLocal || address.IsIPv6Teredo)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        var isGlobalUnicast = (b[0] & 0xE0) == 0x20;
        if (!isGlobalUnicast)
        {
            return false;
        }

        var isDocumentation = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8; // 2001:db8::/32
        var isOrchid = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && (b[3] & 0xF0) == 0x10;  // 2001:10::/28
        var isBenchmarking = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x02 && b[4] == 0 && b[5] == 0; // 2001:2::/48
        if (isDocumentation || isOrchid || isBenchmarking)
        {
            return false;
        }

        // 6to4 (2002::/16) embeds an IPv4 address; it is only as public as that address.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicIPv4([b[2], b[3], b[4], b[5]]);
        }

        return true;
    }
}
