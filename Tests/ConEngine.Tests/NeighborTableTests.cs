using ConInfrastructure.Discovery;

namespace ConEngine.Tests;

public class NeighborTableTests
{
    private const string Linux = """
        IP address       HW type     Flags       HW address            Mask     Device
        192.168.1.1      0x1         0x2         aa:bb:cc:00:11:22     *        eth0
        192.168.1.20     0x1         0x2         AA:BB:CC:00:11:33     *        eth0
        192.168.1.99     0x1         0x0         00:00:00:00:00:00     *        eth0
        192.168.1.77     0x1         0x2         ff:ff:ff:ff:ff:ff     *        eth0
        """;

    private const string Windows = """

        Interface: 192.168.1.50 --- 0x5
          Internet Address      Physical Address      Type
          192.168.1.1           aa-bb-cc-00-11-22     dynamic
          192.168.1.20          aa-bb-cc-00-11-33     dynamic
          192.168.1.255         ff-ff-ff-ff-ff-ff     static
          224.0.0.22            01-00-5e-00-00-16     static
          239.255.255.250       01-00-5e-7f-ff-fa     static
        """;

    private const string Mac = """
        ? (192.168.1.1) at aa:bb:cc:0:11:22 on en0 ifscope [ethernet]
        ? (192.168.1.20) at (incomplete) on en0 ifscope [ethernet]
        ? (192.168.1.30) at a:b:c:d:e:f on en0 ifscope [ethernet]
        """;

    [Fact]
    public void Linux_proc_net_arp_yields_complete_unicast_entries_only()
    {
        var entries = NeighborParser.ParseProcNetArp(Linux);
        Assert.Equal(
            [new NeighborEntry("192.168.1.1", "aa:bb:cc:00:11:22"), new NeighborEntry("192.168.1.20", "aa:bb:cc:00:11:33")],
            entries);
    }

    [Fact]
    public void Windows_arp_a_skips_broadcast_and_multicast_and_normalizes_dashes()
    {
        var entries = NeighborParser.ParseArpCommand(Windows);
        Assert.Equal(
            [new NeighborEntry("192.168.1.1", "aa:bb:cc:00:11:22"), new NeighborEntry("192.168.1.20", "aa:bb:cc:00:11:33")],
            entries);
    }

    [Fact]
    public void Mac_arp_an_pads_short_octets_and_skips_incomplete_entries()
    {
        var entries = NeighborParser.ParseArpCommand(Mac);
        Assert.Equal(
            [new NeighborEntry("192.168.1.1", "aa:bb:cc:00:11:22"), new NeighborEntry("192.168.1.30", "0a:0b:0c:0d:0e:0f")],
            entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("IP address HW type Flags HW address Mask Device")]
    [InlineData("garbage garbage garbage garbage")]
    [InlineData("999.999.999.999 aa-bb-cc-dd-ee-ff dynamic")]
    [InlineData("Interface: 192.168.1.50 --- 0x5\n  Internet Address      Physical Address      Type\n")]
    [InlineData("192.168.1.1 gg-hh-ii-jj-kk-ll dynamic")]
    public void Garbage_never_throws_and_yields_nothing(string text)
    {
        Assert.Empty(NeighborParser.ParseProcNetArp(text));
        Assert.Empty(NeighborParser.ParseArpCommand(text));
    }

    [Fact]
    public void The_system_reader_never_throws_whatever_the_machine_has()
    {
        var entries = new SystemNeighborTable().Read();
        Assert.NotNull(entries);
        Assert.All(entries, e => Assert.Matches("^([0-9a-f]{2}:){5}[0-9a-f]{2}$", e.Mac));
    }
}
