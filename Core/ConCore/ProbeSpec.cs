using System.Net;
using System.Net.Sockets;

namespace ConCore;

public enum ProbeKind { Icmp, Tcp }

/// <summary>
/// How a target is probed: ICMP echo (the default) or a TCP connect where
/// any stack response — SYN-ACK or RST — counts as a reply. Stored in
/// configuration as one optional string ("tcp:80"); absent means ICMP, so
/// every pre-existing config, snapshot, and seed is valid unchanged.
/// </summary>
public readonly record struct ProbeSpec(ProbeKind Kind, int Port = 0)
{
    public static ProbeSpec Icmp => default;

    public static bool TryParse(string? text, out ProbeSpec spec)
    {
        spec = Icmp;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var parts = text.Trim().ToLowerInvariant().Split(':');
        if (parts is ["icmp"])
            return true;
        if (parts is ["tcp", var portText] &&
            int.TryParse(portText, out var port) && port is >= 1 and <= 65535)
        {
            spec = new ProbeSpec(ProbeKind.Tcp, port);
            return true;
        }
        return false;
    }

    public override string ToString() => Kind == ProbeKind.Tcp ? $"tcp:{Port}" : "icmp";

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

    public static bool IsPrivate(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
            return false; // hostname: treat as public
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false; // IPv6 unsupported end to end
        if (IPAddress.IsLoopback(ip))
            return true;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }
}
