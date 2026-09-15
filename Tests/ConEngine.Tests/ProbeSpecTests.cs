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
}
