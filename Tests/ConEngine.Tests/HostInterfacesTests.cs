using System.Net;
using System.Net.NetworkInformation;
using ConInfrastructure.Net;

namespace ConEngine.Tests;

public class HostInterfacesTests
{
    private static HostInterface Nic(string name, NetworkInterfaceType type, OperationalStatus status,
        params string[] gateways) =>
        new(name, type, status, [], gateways.Select(IPAddress.Parse).ToList(), []);

    [Fact]
    public void Usable_drops_down_loopback_and_tunnel_interfaces()
    {
        var kept = HostInterfaces.Usable([
            Nic("up", NetworkInterfaceType.Ethernet, OperationalStatus.Up),
            Nic("down", NetworkInterfaceType.Ethernet, OperationalStatus.Down),
            Nic("lo", NetworkInterfaceType.Loopback, OperationalStatus.Up),
            Nic("tun", NetworkInterfaceType.Tunnel, OperationalStatus.Up),
        ]);

        Assert.Equal(["up"], kept.Select(i => i.Name));
    }

    [Fact]
    public void Usable_keeps_only_real_ipv4_gateways()
    {
        var kept = HostInterfaces.Usable([
            Nic("mixed", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "0.0.0.0", "fe80::1", "192.168.1.1"),
            Nic("zero", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "0.0.0.0"),
        ]);

        Assert.Equal(2, kept.Count);
        Assert.Equal([IPAddress.Parse("192.168.1.1")], kept[0].Gateways);
        Assert.Empty(kept[1].Gateways);
    }
}
