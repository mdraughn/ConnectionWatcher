using ConCore;

namespace ConEngine.Tests;

public class ProbeSpecTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("icmp")]
    [InlineData("ICMP")]
    public void Absent_or_icmp_parses_to_default(string? text)
    {
        Assert.True(ProbeSpec.TryParse(text, out var spec));
        Assert.Equal(ProbeSpec.Icmp, spec);
        Assert.Null(spec.ToConfigString());
    }

    [Theory]
    [InlineData("tcp:80", 80)]
    [InlineData("TCP:443", 443)]
    [InlineData(" tcp:65535 ", 65535)]
    public void Tcp_with_port_parses(string text, int port)
    {
        Assert.True(ProbeSpec.TryParse(text, out var spec));
        Assert.Equal(new ProbeSpec(ProbeKind.Tcp, port), spec);
        Assert.Equal($"tcp:{port}", spec.ToConfigString());
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("tcp:")]
    [InlineData("tcp:0")]
    [InlineData("tcp:65536")]
    [InlineData("tcp:http")]
    [InlineData("udp:53")]
    [InlineData("tcp:80:443")]
    public void Nonsense_is_rejected(string text) =>
        Assert.False(ProbeSpec.TryParse(text, out _));

    [Theory]
    [InlineData("10.1.10.1", 8080, true)]       // RFC1918: any port
    [InlineData("192.168.1.1", 12345, true)]
    [InlineData("172.16.0.1", 1, true)]
    [InlineData("127.0.0.1", 9999, true)]        // loopback
    [InlineData("169.254.1.1", 5000, true)]      // link-local
    [InlineData("8.8.8.8", 443, true)]           // public, polite port
    [InlineData("8.8.8.8", 53, true)]
    [InlineData("8.8.8.8", 8080, false)]         // public, impolite port
    [InlineData("example.com", 80, true)]        // hostname = public, polite ok
    [InlineData("example.com", 8080, false)]     // hostname = public, impolite no
    [InlineData("::1", 9999, false)]              // IPv6 loopback is public → impolite port rejected
    public void Port_policy_by_address_class(string address, int port, bool allowed)
    {
        var ok = ProbePolicy.IsAllowed(address, new ProbeSpec(ProbeKind.Tcp, port), out var reason);
        Assert.Equal(allowed, ok);
        if (!allowed) Assert.Contains("80", reason);
    }

    [Fact]
    public void Icmp_is_always_allowed() =>
        Assert.True(ProbePolicy.IsAllowed("8.8.8.8", ProbeSpec.Icmp, out _));

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("100.64.0.1", true)]     // CGNAT: carrier space, not the user's LAN but not public either
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("example.com", false)]
    public void IsPrivateOrCarrier_covers_rfc1918_linklocal_loopback_and_cgnat(string address, bool expected)
    {
        Assert.Equal(expected, ConCore.ProbePolicy.IsPrivateOrCarrier(address));
    }

    [Fact]
    public void IsPrivate_still_excludes_cgnat_so_the_tcp_port_policy_is_unchanged()
    {
        Assert.False(ConCore.ProbePolicy.IsPrivate("100.64.0.1"));
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("DNS")]
    [InlineData(" dns ")]
    public void Dns_parses_and_prints_as_dns(string text)
    {
        Assert.True(ProbeSpec.TryParse(text, out var spec));
        Assert.Equal(new ProbeSpec(ProbeKind.Dns), spec);
        Assert.Equal("dns", spec.ToString());
        Assert.Equal("dns", spec.ToConfigString());
    }

    [Theory]
    [InlineData("dns:53")]
    [InlineData("dns:")]
    public void Dns_takes_no_port(string text) =>
        Assert.False(ProbeSpec.TryParse(text, out _));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("10.1.10.1")]
    [InlineData("dns.example.net")]
    public void Dns_is_allowed_everywhere(string address) =>
        Assert.True(ProbePolicy.IsAllowed(address, new ProbeSpec(ProbeKind.Dns), out _));

    [Theory]
    [InlineData("172.15.255.255", false, false, false)]
    [InlineData("172.16.0.0", true, true, true)]
    [InlineData("172.31.255.255", true, true, true)]
    [InlineData("172.32.0.0", false, false, false)]
    [InlineData("192.168.0.1", true, true, true)]
    [InlineData("192.169.0.1", false, false, false)]
    [InlineData("100.63.255.255", false, false, false)]
    [InlineData("100.64.0.0", false, true, false)]        // CGNAT: carrier only, on purpose
    [InlineData("100.127.255.255", false, true, false)]
    [InlineData("100.128.0.0", false, false, false)]
    [InlineData("127.0.0.1", true, true, false)]           // loopback and link-local are not RFC1918
    [InlineData("169.254.1.1", true, true, false)]
    // AUDIT: an IPv4-mapped IPv6 address is not unwrapped by any of the three predicates (all treat IPv6 as
    // public); pinned as-is. Is that intended for an address a dual-stack socket could report?
    [InlineData("::ffff:10.0.0.1", false, false, false)]
    public void Private_address_predicates_by_range_edge(string address, bool isPrivate, bool isPrivateOrCarrier, bool isRfc1918)
    {
        // The three predicates differ on purpose; merging them would change the TCP port policy,
        // the traceroute/ASN classification or the LAN sweep.
        Assert.Equal(isPrivate, ProbePolicy.IsPrivate(address));
        Assert.Equal(isPrivateOrCarrier, ProbePolicy.IsPrivateOrCarrier(address));
        Assert.Equal(isRfc1918, ConInfrastructure.Discovery.LanScanner.IsRfc1918(System.Net.IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("icmp")]
    [InlineData("dns")]
    [InlineData("tcp:1")]
    [InlineData("tcp:65535")]
    public void A_printed_spec_parses_back_to_itself(string text)
    {
        Assert.True(ProbeSpec.TryParse(text, out var spec));
        Assert.Equal(text, spec.ToString());
        Assert.True(ProbeSpec.TryParse(spec.ToString(), out var again));
        Assert.Equal(spec, again);
        Assert.True(ProbeSpec.TryParse(spec.ToConfigString(), out var viaConfig));
        Assert.Equal(spec, viaConfig);
    }

    [Theory]
    [InlineData("tcp:+80")]
    [InlineData("tcp: 80")]
    public void A_sign_or_space_before_the_port_is_tolerated(string text)
    {
        // Pins today's leniency (int.TryParse accepts both); Mark's call, not an accident to fix here.
        Assert.True(ProbeSpec.TryParse(text, out var spec));
        Assert.Equal(new ProbeSpec(ProbeKind.Tcp, 80), spec);
    }
}
