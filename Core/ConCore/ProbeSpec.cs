using System.Net;
using System.Net.Sockets;

namespace ConCore;

public enum ProbeKind { Icmp, Tcp, Dns }

/// <summary>
/// How a target is probed: ICMP echo (the default), a TCP connect where
/// any stack response — SYN-ACK or RST — counts as a reply, or a DNS query
/// (UDP 53) where a well-formed answer counts as a reply. Stored in
/// configuration as one optional string ("tcp:80", "dns"); absent means
/// ICMP, so every pre-existing config, snapshot, and seed is valid unchanged.
/// </summary>
public readonly record struct ProbeSpec(ProbeKind Kind, int Port = 0)
{
    public static ProbeSpec Icmp => default;
    public static ProbeSpec Dns => new(ProbeKind.Dns);

    public static bool TryParse(string? text, out ProbeSpec spec)
    {
        spec = Icmp;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var parts = text.Trim().ToLowerInvariant().Split(':');
        if (parts is ["icmp"])
            return true;
        if (parts is ["dns"])
        {
            spec = Dns;
            return true;
        }
        if (parts is ["tcp", var portText] &&
            int.TryParse(portText, out var port) && port is >= 1 and <= 65535)
        {
            spec = new ProbeSpec(ProbeKind.Tcp, port);
            return true;
        }
        return false;
    }

    public override string ToString() => Kind switch
    {
        ProbeKind.Tcp => $"tcp:{Port}",
        ProbeKind.Dns => "dns",
        _ => "icmp",
    };

    /// <summary>The storage form: null for the ICMP default.</summary>
    public string? ToConfigString() => Kind == ProbeKind.Icmp ? null : ToString();
}

/// <summary>
/// Politeness policy: on your own network probe anything; on the public
/// internet only ports where a bare connect-and-close is indistinguishable
/// from normal client traffic. Hostnames count as public.
/// </summary>
public static class ProbePolicy
{
    public static readonly int[] PublicPorts = [80, 443, 53];

    public static bool IsAllowed(string address, ProbeSpec probe, out string? reason)
    {
        reason = null;
        if (probe.Kind != ProbeKind.Tcp || IsPrivate(address) || PublicPorts.Contains(probe.Port))
            return true;
        reason = $"TCP probes to public addresses are limited to ports " +
                 $"{string.Join(", ", PublicPorts)} — '{address}' is not a private address.";
        return false;
    }

    public static bool IsPrivate(string address) =>
        IPAddress.TryParse(address, out var ip) && IsPrivate(ip); // hostname: treat as public

    /// <summary>RFC 1918 space (IPv4 10/8, 172.16/12, 192.168/16) and nothing else.</summary>
    public static bool IsRfc1918(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>
    /// RFC 1918, loopback, or IPv4 link-local (169.254/16). IPv6 is not private
    /// (IPv6 unsupported end to end).
    /// </summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (IPAddress.IsLoopback(ip))
            return true;
        var b = ip.GetAddressBytes();
        return IsRfc1918(ip) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// <see cref="IsPrivate"/> plus carrier-grade NAT space (100.64.0.0/10):
    /// an address that is not routable on the public internet. Used to
    /// classify traceroute hops and to keep such addresses out of external
    /// lookups. Not used by the TCP port policy, which treats CGNAT as public.
    /// </summary>
    public static bool IsPrivateOrCarrier(string address) =>
        IPAddress.TryParse(address, out var ip) && IsPrivateOrCarrier(ip);

    public static bool IsPrivateOrCarrier(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (IsPrivate(ip))
            return true;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] is >= 64 and <= 127;
    }
}
