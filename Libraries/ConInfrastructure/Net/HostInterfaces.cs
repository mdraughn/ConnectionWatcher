using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ConInfrastructure.Net;

/// <summary>An IPv4 unicast address on a host interface, with its subnet prefix length.</summary>
public sealed record HostAddress(IPAddress Address, int PrefixLength);

/// <summary>One network interface as the host reports it: its IPv4 addresses, gateways and DNS servers.</summary>
public sealed record HostInterface(string Name, NetworkInterfaceType Type, OperationalStatus Status,
    IReadOnlyList<HostAddress> UnicastV4, IReadOnlyList<IPAddress> Gateways, IReadOnlyList<IPAddress> DnsServers);

/// <summary>The one walk over the host's network interfaces (router search, LAN scan, system resolvers).</summary>
public static class HostInterfaces
{
    /// <summary>The usable interfaces, or none when the OS cannot list them.</summary>
    public static IReadOnlyList<HostInterface> Read()
    {
        try
        {
            return Usable(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(Map).ToList());
        }
        catch (NetworkInformationException)
        {
            return []; // No interface information: nothing to find.
        }
    }

    private static HostInterface Map(NetworkInterface nic)
    {
        var properties = nic.GetIPProperties();
        return new HostInterface(nic.Name, nic.NetworkInterfaceType, nic.OperationalStatus,
            properties.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => new HostAddress(u.Address, u.PrefixLength)).ToList(),
            properties.GatewayAddresses.Select(g => g.Address).ToList(),
            properties.DnsAddresses.ToList());
    }

    /// <summary>Up, non-loopback, non-tunnel interfaces; each keeps only its IPv4, non-0.0.0.0 gateways
    /// (some VPN adapters list 0.0.0.0). An interface left with no gateway is still kept.</summary>
    internal static IReadOnlyList<HostInterface> Usable(IEnumerable<HostInterface> all) => all
        .Where(i => i.Status == OperationalStatus.Up &&
                    i.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .Select(i => i with
        {
            Gateways = i.Gateways
                .Where(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any)).ToList()
        })
        .ToList();
}
