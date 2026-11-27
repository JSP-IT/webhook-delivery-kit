using System.Net;
using System.Net.Sockets;

namespace JspIt.WebhookDelivery.Http;

/// <summary>
/// Classifies IP addresses for SSRF protection. Connections are only made to addresses that are
/// publicly routable, unless the destination explicitly allows loopback or specific private networks.
/// </summary>
/// <remarks>
/// The check is applied to the address that is actually connected to (after DNS resolution, inside
/// the HTTP handler's connect callback), so DNS rebinding cannot swap in an internal address after
/// validation.
/// </remarks>
public static class NetworkAddressPolicy
{
    // Non-public IPv4 ranges (RFC 6890 special-purpose registry and friends).
    private static readonly IPNetwork[] BlockedV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),          // "this network"
        IPNetwork.Parse("10.0.0.0/8"),         // private
        IPNetwork.Parse("100.64.0.0/10"),      // carrier-grade NAT
        IPNetwork.Parse("127.0.0.0/8"),        // loopback
        IPNetwork.Parse("169.254.0.0/16"),     // link-local, incl. cloud metadata 169.254.169.254
        IPNetwork.Parse("172.16.0.0/12"),      // private
        IPNetwork.Parse("192.0.0.0/24"),       // IETF protocol assignments
        IPNetwork.Parse("192.0.2.0/24"),       // TEST-NET-1
        IPNetwork.Parse("192.88.99.0/24"),     // 6to4 relay anycast (deprecated)
        IPNetwork.Parse("192.168.0.0/16"),     // private
        IPNetwork.Parse("198.18.0.0/15"),      // benchmarking
        IPNetwork.Parse("198.51.100.0/24"),    // TEST-NET-2
        IPNetwork.Parse("203.0.113.0/24"),     // TEST-NET-3
        IPNetwork.Parse("224.0.0.0/4"),        // multicast
        IPNetwork.Parse("240.0.0.0/4"),        // reserved, incl. 255.255.255.255 broadcast
    ];

    private static readonly IPNetwork[] BlockedV6 =
    [
        IPNetwork.Parse("::/128"),             // unspecified
        IPNetwork.Parse("::1/128"),            // loopback
        IPNetwork.Parse("100::/64"),           // discard-only
        IPNetwork.Parse("2001::/23"),          // IETF protocol assignments (incl. Teredo 2001::/32)
        IPNetwork.Parse("2001:db8::/32"),      // documentation
        IPNetwork.Parse("2002::/16"),          // 6to4 (embeds arbitrary IPv4)
        IPNetwork.Parse("fc00::/7"),           // unique local
        IPNetwork.Parse("fe80::/10"),          // link-local
        IPNetwork.Parse("fec0::/10"),          // site-local (deprecated)
        IPNetwork.Parse("ff00::/8"),           // multicast
    ];

    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork Nat64Local = IPNetwork.Parse("64:ff9b:1::/48");

    /// <summary>Whether <paramref name="address"/> is a loopback address (127.0.0.0/8 or ::1, including IPv4-mapped forms).</summary>
    public static bool IsLoopback(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);
        return IPAddress.IsLoopback(normalized);
    }

    /// <summary>
    /// Whether <paramref name="address"/> is a publicly routable unicast address. IPv4-mapped and
    /// IPv4-compatible IPv6 addresses are judged by their embedded IPv4 address; NAT64 prefixes are
    /// treated as non-public because their target cannot be checked.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);

        if (normalized.AddressFamily == AddressFamily.InterNetwork)
        {
            return !BlockedV4.Any(n => n.Contains(normalized));
        }

        if (normalized.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (Nat64.Contains(normalized) || Nat64Local.Contains(normalized))
            {
                return false;
            }

            // Only the global unicast block 2000::/3 is public; everything else is reserved.
            var first = normalized.GetAddressBytes()[0];
            if ((first & 0xE0) != 0x20)
            {
                return false;
            }

            return !BlockedV6.Any(n => n.Contains(normalized));
        }

        return false;
    }

    /// <summary>Decides whether a connection to <paramref name="address"/> is permitted for a destination.</summary>
    public static bool IsAllowed(IPAddress address, bool allowLoopback, IReadOnlyList<IPNetwork> allowedPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(allowedPrivateNetworks);
        var normalized = Normalize(address);

        if (allowLoopback && IPAddress.IsLoopback(normalized))
        {
            return true;
        }

        foreach (var network in allowedPrivateNetworks)
        {
            if (network.BaseAddress.AddressFamily == normalized.AddressFamily && network.Contains(normalized))
            {
                return true;
            }
        }

        return IsPublic(normalized);
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        // Deprecated IPv4-compatible form ::a.b.c.d (but not :: or ::1).
        var bytes = address.GetAddressBytes();
        var isCompatible = true;
        for (var i = 0; i < 12; i++)
        {
            if (bytes[i] != 0)
            {
                isCompatible = false;
                break;
            }
        }

        if (isCompatible && (bytes[12] | bytes[13]) != 0)
        {
            return new IPAddress(bytes.AsSpan(12, 4));
        }

        // Strip the scope id so link-local checks are not affected by it.
        return address.ScopeId != 0 ? new IPAddress(bytes) : address;
    }
}
