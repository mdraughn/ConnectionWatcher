using System.Net;
using ConCore;
using ConCore.Dns;
using ConEngine.Tests.Fakes;
using ConInfrastructure;
using ConInfrastructure.NameResolution;

namespace ConEngine.Tests;

public class DnsQueryTransceiverTests
{
    private static readonly DnsLookupName Google = new("dns.google", ["8.8.8.8", "8.8.4.4"]);
    private static readonly DnsLookupName Admin = new("example.org", []);

    private static DnsLookupNames Names(params DnsLookupName[] names)
    {
        var list = new DnsLookupNames();
        list.Replace(names);
        return list;
    }

    private static DnsQueryTransceiver Probe(FakeDnsServer server, DnsLookupNames names) =>
        new(IPAddress.Loopback, names, CancellationToken.None, server.Port);

    [Fact]
    public async Task A_name_the_wire_format_cannot_carry_is_BadName_and_nothing_is_sent()
    {
        using var server = new FakeDnsServer();
        using var probe = Probe(server, Names(new DnsLookupName("a..b", [])));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal("BadName (a..b)", result.Message);
        Assert.Equal(0, server.Received);
    }

    [Fact]
    public async Task An_expected_address_is_Verified()
    {
        using var server = new FakeDnsServer();
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.True(result.Success);
        Assert.Equal("Verified (dns.google)", result.Message);
        Assert.Equal(1, server.Received);
    }

    [Fact]
    public async Task A_wrong_address_for_a_seeded_name_is_a_miss()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse("10.0.0.1"))];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal("WrongAnswer: 10.0.0.1 (dns.google)", result.Message);
    }

    [Fact]
    public async Task NXDOMAIN_is_Filtered_for_a_seeded_name_and_a_hit_for_an_admin_name()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NxDomain)];

        using var seeded = Probe(server, Names(Google));
        var filtered = await seeded.PingAsync(2000);
        Assert.True(filtered.Success);
        Assert.Equal("Filtered: NXDOMAIN (dns.google)", filtered.Message);

        using var admin = Probe(server, Names(Admin));
        var hit = await admin.PingAsync(2000);
        Assert.True(hit.Success);
        Assert.Equal("NxDomain (example.org)", hit.Message);
    }

    [Theory]
    [InlineData("0.0.0.0", "Filtered: 0.0.0.0 (dns.google)")]
    [InlineData("127.0.0.1", "Filtered: 127.0.0.1 (dns.google)")]
    public async Task A_blocked_address_for_a_seeded_name_is_a_Filtered_hit(string blocked, string message)
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse(blocked))];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.True(result.Success);
        Assert.Equal(message, result.Message);
    }

    [Fact]
    public async Task An_empty_answer_for_a_seeded_name_is_a_Filtered_hit()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NoError)];
        using var probe = Probe(server, Names(Google));
        var empty = await probe.PingAsync(2000);
        Assert.True(empty.Success);
        Assert.Equal("Filtered: no address (dns.google)", empty.Message);
    }

    [Fact]
    public async Task A_real_wrong_address_is_still_a_WrongAnswer_miss()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse("0.0.0.0"), IPAddress.Parse("10.0.0.1"))];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success); // one real address among the blocked ones: not a clean block
        Assert.Equal("WrongAnswer: 0.0.0.0, 10.0.0.1 (dns.google)", result.Message);
    }

    [Theory]
    [InlineData(DnsRcode.ServFail, "ServFail (example.org)")]
    [InlineData(DnsRcode.Refused, "RcodeRefused (example.org)")]
    [InlineData(DnsRcode.NotImp, "Rcode4 (example.org)")]
    public async Task Error_rcodes_are_misses_named_without_the_bare_word_Refused(DnsRcode rcode, string message)
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, rcode)];
        using var probe = Probe(server, Names(Admin));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.NotEqual("Refused", result.Message); // TargetMonitor counts that word as a TCP refusal
    }

    [Fact]
    public async Task Silence_is_TimedOut()
    {
        using var server = new FakeDnsServer();
        server.Reply = (_, _) => null;
        using var probe = Probe(server, Names(Admin));
        var result = await probe.PingAsync(300);
        Assert.False(result.Success);
        Assert.Equal("TimedOut (example.org)", result.Message);
    }

    [Fact]
    public async Task Garbage_is_Malformed()
    {
        using var server = new FakeDnsServer();
        server.Reply = (_, _) => [new byte[] { 1, 2, 3 }];
        using var probe = Probe(server, Names(Admin));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal("Malformed (example.org)", result.Message);
    }

    [Fact]
    public async Task A_stray_reply_with_another_id_is_ignored_and_the_right_one_is_taken()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) =>
        [
            FakeDnsWire.Response((ushort)(id ^ 0x5555), name, DnsRcode.ServFail),
            FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse("8.8.4.4")),
        ];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.True(result.Success);
        Assert.Equal("Verified (dns.google)", result.Message);
    }

    [Fact]
    public async Task No_lookup_names_is_a_miss_that_sends_nothing()
    {
        using var server = new FakeDnsServer();
        using var probe = Probe(server, Names());
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal("NoLookupNames", result.Message);
        Assert.Equal(0, server.Received);
    }

    [Fact]
    public async Task Names_rotate_across_probes()
    {
        using var server = new FakeDnsServer();
        var asked = new List<string>();
        server.Reply = (id, name) => { lock (asked) asked.Add(name); return [FakeDnsWire.Response(id, name, DnsRcode.NoError)]; };
        using var probe = Probe(server, Names(Admin, new DnsLookupName("second.test", [])));
        await probe.PingAsync(2000);
        await probe.PingAsync(2000);
        await probe.PingAsync(2000);
        Assert.Equal(["example.org", "second.test", "example.org"], asked);
    }

    [Fact]
    public void The_factory_returns_the_dns_transceiver_for_the_dns_probe()
    {
        var factory = new PingTransceiverFactoryIcmp(DnsLookupNames.Empty);
        using var dns = factory.Create(IPAddress.Loopback, ProbeSpec.Dns, CancellationToken.None);
        Assert.IsType<DnsQueryTransceiver>(dns);
        using var icmp = factory.Create(IPAddress.Loopback, ProbeSpec.Icmp, CancellationToken.None);
        Assert.IsNotType<DnsQueryTransceiver>(icmp);
    }

    [Theory]
    [InlineData(DnsRcode.ServFail, "ServFail (dns.google)")]
    [InlineData(DnsRcode.Refused, "RcodeRefused (dns.google)")]
    [InlineData(DnsRcode.NotImp, "Rcode4 (dns.google)")]
    public async Task A_seeded_name_answered_with_an_error_rcode_is_a_miss(DnsRcode rcode, string message)
    {
        // Only NXDOMAIN and a blocked NOERROR answer are a working resolver that filters;
        // an error rcode is a broken resolver and must stay a miss, or DnsDown never fires.
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, rcode)];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.False(result.Message.StartsWith("Filtered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_expected_address_among_others_is_Verified()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NoError,
            IPAddress.Parse("10.0.0.1"), IPAddress.Parse("8.8.4.4"))];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.True(result.Success);
        Assert.Equal("Verified (dns.google)", result.Message);
    }

    [Fact]
    public async Task Engine_cancellation_throws_instead_of_reporting_a_miss()
    {
        using var server = new FakeDnsServer();
        server.Reply = (_, _) => null; // a silent resolver
        using var cts = new CancellationTokenSource();
        using var probe = new DnsQueryTransceiver(IPAddress.Loopback, Names(Admin), cts.Token, server.Port);
        var task = probe.PingAsync(5000);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (server.Received < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(1, server.Received);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Nothing_listening_on_53_is_a_prompt_miss_not_Refused()
    {
        int port;
        using (var closed = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)closed.Client.LocalEndPoint!).Port;
        using var probe = new DnsQueryTransceiver(IPAddress.Loopback, Names(Admin), CancellationToken.None, port);
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.NotEqual("Refused", result.Message);
        Assert.EndsWith("(example.org)", result.Message);
    }
}
