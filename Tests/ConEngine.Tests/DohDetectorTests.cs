using ConCore.Dns;
using ConInfrastructure.NameResolution;

namespace ConEngine.Tests;

public class DohDetectorTests
{
    private static DohRegistryView View(string[]? perInterface = null, int? policy = null, int? auto = null, string[]? wellKnown = null) =>
        new(perInterface ?? [], policy, auto, wellKnown ?? []);

    [Fact]
    public void Reports_only_the_resolvers_the_source_lists_case_insensitively()
    {
        var detector = new WindowsDohDetector(() => View(perInterface: ["1.1.1.1", "FE80::1"]));
        Assert.Equal(["1.1.1.1", "fe80::1"], detector.AddressesUsingDoh(["10.0.0.1", "1.1.1.1", "fe80::1"]));
    }

    [Fact]
    public void An_IPv6_server_matches_however_either_side_spells_it()
    {
        var detector = new WindowsDohDetector(() => View(perInterface: ["2001:4860:4860:0000:0000:0000:0000:8888"]));
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

    [Fact]
    public void Require_policy_reports_every_resolver()
    {
        Assert.Equal(["10.1.10.1", "1.1.1.1"], WindowsDohDetector.Decide(View(policy: 3), ["10.1.10.1", "1.1.1.1"]));
    }

    [Fact]
    public void Allow_policy_or_auto_doh_reports_only_well_known_servers()
    {
        Assert.Equal(["1.1.1.1"], WindowsDohDetector.Decide(View(policy: 2), ["10.1.10.1", "1.1.1.1"]));
        Assert.Equal(["8.8.8.8"], WindowsDohDetector.Decide(View(auto: 2), ["192.0.2.53", "8.8.8.8"]));
        Assert.Equal(["2620:fe::fe"], WindowsDohDetector.Decide(View(auto: 2), ["2620:fe::fe"]));
        Assert.Empty(WindowsDohDetector.Decide(View(auto: 1), ["8.8.8.8"]));
        Assert.Empty(WindowsDohDetector.Decide(View(), ["8.8.8.8"]));
    }

    [Fact]
    public void A_registry_listed_well_known_server_counts_under_allow()
    {
        Assert.Equal(["192.0.2.53"], WindowsDohDetector.Decide(View(auto: 2, wellKnown: ["192.0.2.53"]), ["192.0.2.53", "192.0.2.54"]));
    }

    [Fact]
    public void Prohibit_policy_switches_off_auto_doh_but_not_a_per_interface_entry()
    {
        Assert.Empty(WindowsDohDetector.Decide(View(policy: 1, auto: 2), ["8.8.8.8"]));
        Assert.Equal(["10.1.10.1"], WindowsDohDetector.Decide(View(perInterface: ["10.1.10.1"], policy: 1, auto: 2), ["10.1.10.1", "8.8.8.8"]));
    }

    [Fact]
    public void A_zone_id_on_a_registry_key_does_not_stop_the_match()
    {
        Assert.Equal(["fe80::1"], WindowsDohDetector.Decide(View(perInterface: ["fe80::1%12"]), ["fe80::1"]));
    }
}
