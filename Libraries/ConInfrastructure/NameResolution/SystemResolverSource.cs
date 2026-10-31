using System.Net;
using System.Net.Sockets;
using ConInfrastructure.Net;

namespace ConInfrastructure.NameResolution;

/// <summary>One resolver the host is configured to use; Docker's embedded forwarder is flagged for naming.</summary>
public sealed record SystemResolver(IPAddress Address, bool IsDockerEmbedded);

public interface ISystemResolverSource
{
    /// <summary>The resolvers this host resolves through, in the OS's order, IPv4 first. Never throws.</summary>
    IReadOnlyList<SystemResolver> Read();
}

/// <summary>
/// The way this host resolves (spec 2026-10-10 §4.3, D1): Windows' DNS
/// addresses on the interfaces that own a default route, or Linux's
/// resolv.conf nameservers. Inside a Docker bridge network that is 127.0.0.11,
/// Docker's forwarder, which forwards to the host's resolvers — still "the way
/// this host resolves".
/// </summary>
public sealed class SystemResolverSource(Func<IEnumerable<IPAddress>> raw) : ISystemResolverSource
{
    private static readonly IPAddress DockerEmbedded = IPAddress.Parse("127.0.0.11");

    public SystemResolverSource() : this(OperatingSystem.IsWindows() ? WindowsResolvers : LinuxResolvers)
    {
    }

    public IReadOnlyList<SystemResolver> Read()
    {
        IEnumerable<IPAddress> addresses;
        try
        {
            addresses = raw().ToList();
        }
        catch (Exception)
        {
            return []; // no interfaces, no resolv.conf: nothing found, not a crash
        }
        return addresses
            .Where(a => !IsWindowsPlaceholder(a))
            .Distinct()
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1) // stable: keeps OS order within a family
            .Select(a => new SystemResolver(a, a.Equals(DockerEmbedded)))
            .ToList();
    }

    // Windows lists the deprecated site-local fec0:0:0:ffff::1-3 as DNS on NICs with no IPv6 DNS; they never answer.
    private static bool IsWindowsPlaceholder(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 &&
        address.GetAddressBytes().AsSpan(0, 8).SequenceEqual(new byte[] { 0xfe, 0xc0, 0, 0, 0, 0, 0xff, 0xff });

    internal static IEnumerable<IPAddress> WindowsResolvers() =>
        // Only interfaces that carry a real IPv4 default route resolve for the host; some VPN
        // adapters list 0.0.0.0 as a gateway.
        HostInterfaces.Read().Where(i => i.Gateways.Count > 0).SelectMany(i => i.DnsServers);

    internal static IEnumerable<IPAddress> LinuxResolvers() =>
        ParseResolvConf(File.ReadLines("/etc/resolv.conf"));

    /// <summary>"nameserver X" lines, in order; comments, options and unparsable entries are skipped;
    /// an IPv6 zone suffix ("%eth0") is kept when .NET parses it, dropped otherwise.</summary>
    internal static IEnumerable<IPAddress> ParseResolvConf(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Split('#', 2)[0].Split(';', 2)[0].Trim();
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("nameserver", StringComparison.OrdinalIgnoreCase))
                continue;
            // Keep the scope when .NET accepts the full token (a link-local resolver needs it to
            // be reachable); otherwise drop the "%zone" and try the bare address.
            if (IPAddress.TryParse(parts[1], out var address) ||
                IPAddress.TryParse(parts[1].Split('%', 2)[0], out address))
                yield return address;
        }
    }
}
