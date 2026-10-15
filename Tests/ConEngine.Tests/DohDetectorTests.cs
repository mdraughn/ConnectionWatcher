using ConCore.Dns;
using ConInfrastructure.NameResolution;

namespace ConEngine.Tests;

public class DohDetectorTests
{
    [Fact]
    public void Reports_only_the_resolvers_the_source_lists_case_insensitively()
    {
        var detector = new WindowsDohDetector(() => ["1.1.1.1", "FE80::1"]);
        Assert.Equal(["1.1.1.1", "fe80::1"], detector.AddressesUsingDoh(["10.0.0.1", "1.1.1.1", "fe80::1"]));
    }

    [Fact]
    public void An_IPv6_server_matches_however_either_side_spells_it()
    {
        var detector = new WindowsDohDetector(() => ["2001:4860:4860:0000:0000:0000:0000:8888"]);
        Assert.Equal(["2001:4860:4860::8888"], detector.AddressesUsingDoh(["2001:4860:4860::8888", "2001:4860:4860::8844"]));
    }

    [Fact]
    public void A_throwing_source_reads_as_nothing()
    {
        var detector = new WindowsDohDetector(() => throw new UnauthorizedAccessException("registry"));
        Assert.Empty(detector.AddressesUsingDoh(["1.1.1.1"]));
    }

    [Fact]
    public void The_platform_detector_never_throws_on_this_machine() =>
        Assert.NotNull(DohConfiguration.ForThisPlatform().AddressesUsingDoh(["1.1.1.1", "8.8.8.8"]));

    [Fact]
    public void Registry_values_are_read_as_doh_only_when_DohFlags_is_non_zero()
    {
        // The parser over a fake registry walk: (interface, address, DohFlags)
        var rows = new (string Iface, string Address, object? Flags)[]
        {
            ("{A}", "1.1.1.1", 1), ("{A}", "8.8.8.8", 0), ("{B}", "9.9.9.9", 2L), ("{B}", "4.4.4.4", null), ("{B}", "5.5.5.5", "x"),
        };
        Assert.Equal(["1.1.1.1", "9.9.9.9"], WindowsDohDetector.AddressesFrom(rows));
    }
}
