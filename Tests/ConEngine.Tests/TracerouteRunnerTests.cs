using System.Net;
using ConInfrastructure.Asn;
using ConInfrastructure.Traceroute;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class TracerouteRunnerTests
{
    /// <summary>Scripted probe: hop addresses per TTL; null = timeout at that TTL.</summary>
    private sealed class FakeProbe(params string?[] hops) : ITtlProbe
    {
        public Task<TtlProbeResult> ProbeAsync(IPAddress destination, int ttl, int timeoutMs, CancellationToken ct)
        {
            if (ttl > hops.Length)
                return Task.FromResult(new TtlProbeResult(destination, 30, ReachedDestination: true, TimedOut: false));
            var hop = hops[ttl - 1];
            if (hop is null)
                return Task.FromResult(new TtlProbeResult(null, 0, false, TimedOut: true));
            var address = IPAddress.Parse(hop);
            bool reached = address.Equals(destination);
            return Task.FromResult(new TtlProbeResult(address, ttl * 10, reached, false));
        }
    }

    [Fact]
    public async Task Ordered_hops_ending_at_destination()
    {
        var runner = new TracerouteRunner(new FakeProbe("10.0.0.1", "198.51.100.11", "8.8.8.8"),
            NullAsnLookup.Instance, NullLogger<TracerouteRunner>.Instance);
        var hops = await runner.RunAsync("8.8.8.8", lookupOperators: false, CancellationToken.None);

        Assert.Equal(3, hops.Count);
        Assert.Equal([1, 2, 3], hops.Select(h => h.Ttl));
        Assert.Equal("10.0.0.1", hops[0].Address);
        Assert.False(hops[0].ReachedDestination);
        Assert.Equal("8.8.8.8", hops[2].Address);
        Assert.True(hops[2].ReachedDestination);
    }

    [Fact]
    public async Task Silent_hop_is_preserved_as_null_row()
    {
        var runner = new TracerouteRunner(new FakeProbe("10.0.0.1", null, "8.8.8.8"),
            NullAsnLookup.Instance, NullLogger<TracerouteRunner>.Instance);
        var hops = await runner.RunAsync("8.8.8.8", lookupOperators: false, CancellationToken.None);

        Assert.Equal(3, hops.Count);
        Assert.Null(hops[1].Address);
        Assert.Null(hops[1].RttMs);
        Assert.True(hops[2].ReachedDestination);
    }

    [Fact]
    public async Task Unresolvable_host_throws()
    {
        var runner = new TracerouteRunner(new FakeProbe("8.8.8.8"), NullAsnLookup.Instance, NullLogger<TracerouteRunner>.Instance);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            runner.RunAsync("definitely-not-a-real-host.invalid", lookupOperators: false, CancellationToken.None));
    }

    [Fact]
    public async Task Stops_at_max_ttl_when_destination_never_answers()
    {
        // Every TTL returns the same non-destination hop; runner must stop at 30.
        var hops = Enumerable.Repeat<string?>("10.0.0.1", 40).ToArray();
        var runner = new TracerouteRunner(new FakeProbe(hops), NullAsnLookup.Instance, NullLogger<TracerouteRunner>.Instance);
        var result = await runner.RunAsync("8.8.8.8", lookupOperators: false, CancellationToken.None);
        Assert.Equal(30, result.Count);
        Assert.False(result.Last().ReachedDestination);
    }

    private sealed class FakeAsn : IAsnLookup
    {
        public List<string> Asked { get; } = [];
        public Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct)
        {
            lock (Asked) Asked.Add(address.ToString());
            return Task.FromResult<AsnInfo?>(address.ToString() == "198.51.100.11"
                ? new AsnInfo(64496, "EXAMPLE-ISP", "US") : null);
        }
    }

    [Fact]
    public async Task Lookup_fills_asn_on_path_and_destination_hops_only()
    {
        var asn = new FakeAsn();
        var runner = new TracerouteRunner(new FakeProbe("10.0.0.1", "198.51.100.11", "8.8.8.8"), asn,
            NullLogger<TracerouteRunner>.Instance);
        var hops = await runner.RunAsync("8.8.8.8", lookupOperators: true, CancellationToken.None);

        Assert.Equal(["198.51.100.11", "8.8.8.8"], asn.Asked.OrderBy(a => a));
        Assert.Null(hops[0].Asn);
        Assert.Equal(64496, hops[1].Asn);
        Assert.Equal("EXAMPLE-ISP", hops[1].AsName);
        Assert.Null(hops[2].Asn); // the fake answers null for 8.8.8.8: a miss leaves the fields empty
    }

    [Fact]
    public async Task Lookup_skips_private_and_unanswered_hops()
    {
        var asn = new FakeAsn();
        var runner = new TracerouteRunner(new FakeProbe("192.168.1.1", null, "192.168.1.50"), asn,
            NullLogger<TracerouteRunner>.Instance);
        var hops = await runner.RunAsync("192.168.1.50", lookupOperators: true, CancellationToken.None);

        Assert.Equal(HopKind.Destination, hops[2].Kind);
        Assert.Empty(asn.Asked); // Review Focus 1: a private destination is never sent out
    }

    private sealed class ThrowingAsn : IAsnLookup
    {
        public Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task A_throwing_lookup_leaves_the_hops_unchanged()
    {
        var runner = new TracerouteRunner(new FakeProbe("10.0.0.1", "198.51.100.11", "8.8.8.8"), new ThrowingAsn(),
            NullLogger<TracerouteRunner>.Instance);
        var hops = await runner.RunAsync("8.8.8.8", lookupOperators: true, CancellationToken.None);

        Assert.Equal(3, hops.Count);
        Assert.All(hops, h => Assert.Null(h.Asn));
    }

    [Fact]
    public async Task Lookup_off_asks_nothing()
    {
        var asn = new FakeAsn();
        var runner = new TracerouteRunner(new FakeProbe("10.0.0.1", "198.51.100.11", "8.8.8.8"), asn,
            NullLogger<TracerouteRunner>.Instance);
        await runner.RunAsync("8.8.8.8", lookupOperators: false, CancellationToken.None);
        Assert.Empty(asn.Asked);
    }

    [Fact]
    public async Task StopWhen_ends_the_walk_after_the_hop_that_satisfies_it()
    {
        var runner = new TracerouteRunner(new FakeProbe("10.0.0.1", "198.51.100.11", "198.51.100.12", "8.8.8.8"),
            NullAsnLookup.Instance, NullLogger<TracerouteRunner>.Instance);

        var hops = await runner.RunAsync("8.8.8.8", lookupOperators: false, stopWhen: h => h.Ttl == 2, CancellationToken.None);

        Assert.Equal([1, 2], hops.Select(h => h.Ttl));
        Assert.False(hops.Last().ReachedDestination);
        Assert.NotNull(hops.Last().Kind);   // the stopped walk is still classified
    }

    /// <summary>Answers each TTL's three probes from a script, in call order, and counts calls per TTL.</summary>
    private sealed class ScriptedProbe(Func<int, int, TtlProbeResult> answer) : ITtlProbe
    {
        public Dictionary<int, int> CallsPerTtl { get; } = [];
        public Task<TtlProbeResult> ProbeAsync(IPAddress destination, int ttl, int timeoutMs, CancellationToken ct)
        {
            int n;
            lock (CallsPerTtl)
            {
                n = CallsPerTtl.GetValueOrDefault(ttl);
                CallsPerTtl[ttl] = n + 1;
            }
            return Task.FromResult(answer(ttl, n));
        }
    }

    [Fact]
    public async Task Each_hop_is_probed_three_times_first_address_best_rtt()
    {
        // TTL 1: timeout, then 192.0.2.1 at 12 ms, then 192.0.2.2 at 30 ms; only the 2nd probe "reached".
        // (The best rtt is deliberately not the last one answered, so "last wins" cannot pass.)
        // Addresses stay in documentation ranges; reverse DNS is real here, so nothing asserts on the host name.
        var probe = new ScriptedProbe((_, n) => n switch
        {
            0 => new TtlProbeResult(null, 0, false, TimedOut: true),
            1 => new TtlProbeResult(IPAddress.Parse("192.0.2.1"), 12, ReachedDestination: true, TimedOut: false),
            _ => new TtlProbeResult(IPAddress.Parse("192.0.2.2"), 30, false, false),
        });
        var runner = new TracerouteRunner(probe, NullAsnLookup.Instance, NullLogger<TracerouteRunner>.Instance);

        var hops = await runner.RunAsync("198.51.100.9", lookupOperators: false, CancellationToken.None);

        var hop = Assert.Single(hops);
        Assert.Equal(3, probe.CallsPerTtl[1]);
        Assert.Equal("192.0.2.1", hop.Address);   // the first address seen, not the last
        Assert.Equal(12, hop.RttMs);              // the best rtt across the probes that answered
        Assert.True(hop.ReachedDestination);      // any one probe reaching is enough
    }
}
