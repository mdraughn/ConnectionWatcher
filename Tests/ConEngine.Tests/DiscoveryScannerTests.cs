using System.Net;
using ConCore.Discovery;
using ConInfrastructure.Asn;
using ConInfrastructure.Discovery;
using ConInfrastructure.Traceroute;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class DiscoveryScannerTests
{
    private sealed class FakeGateways(params GatewayCandidate[] candidates) : IGatewayDiscovery
    {
        public Task<IReadOnlyList<GatewayCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GatewayCandidate>>(candidates);
    }

    /// <summary>Hop addresses per TTL (null = silent); beyond the list the destination answers.</summary>
    private sealed class ScriptProbe(params string?[] hops) : ITtlProbe
    {
        public int HighestTtl { get; private set; }

        public Task<TtlProbeResult> ProbeAsync(IPAddress destination, int ttl, int timeoutMs, CancellationToken ct)
        {
            HighestTtl = Math.Max(HighestTtl, ttl);
            if (ttl > hops.Length)
                return Task.FromResult(new TtlProbeResult(destination, 30, ReachedDestination: true, TimedOut: false));
            var hop = hops[ttl - 1];
            if (hop is null)
                return Task.FromResult(new TtlProbeResult(null, 0, false, TimedOut: true));
            return Task.FromResult(new TtlProbeResult(IPAddress.Parse(hop), ttl * 10, false, false));
        }
    }

    private static DiscoveryScanner Scanner(IGatewayDiscovery gateways, ScriptProbe? probe = null) =>
        new(gateways, new TracerouteRunner(probe ?? new ScriptProbe(), NullAsnLookup.Instance,
            NullLogger<TracerouteRunner>.Instance));

    [Fact]
    public async Task Router_search_keeps_the_default_route_even_without_a_ping_reply_and_reachable_probes()
    {
        var scanner = Scanner(new FakeGateways(
            new GatewayCandidate("192.0.2.1", "default-route", false, null),
            new GatewayCandidate("192.0.2.254", "probe", true, 4),
            new GatewayCandidate("198.51.100.1", "probe", false, null)));

        var found = await scanner.FindRouterAsync(CancellationToken.None);

        Assert.Equal(["192.0.2.1", "192.0.2.254"], found.Select(c => c.Address));
        Assert.All(found, c => Assert.Equal(DiscoveryKind.Gateway, c.Kind));
        Assert.Equal(DiscoveryMethod.DefaultRoute, found[0].Method);
        Assert.Contains("does not answer", found[0].Evidence);
        Assert.Equal(DiscoveryMethod.CommonAddress, found[1].Method);
        Assert.Contains("4 ms", found[1].Evidence);
    }

    [Fact]
    public async Task Router_search_with_nothing_found_returns_an_empty_list()
    {
        var scanner = Scanner(new FakeGateways(new GatewayCandidate("198.51.100.1", "probe", false, null)));
        Assert.Empty(await scanner.FindRouterAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Edge_search_finds_the_first_public_hop_and_stops_walking_there()
    {
        var probe = new ScriptProbe("192.168.1.1", "100.64.0.1", "203.0.113.5", "203.0.113.6", "203.0.113.7");
        var scanner = Scanner(new FakeGateways(), probe);

        var found = await scanner.FindNetworkEdgeAsync("198.51.100.77", CancellationToken.None);

        var edge = Assert.Single(found);
        Assert.Equal("203.0.113.5", edge.Address);
        Assert.Equal(DiscoveryKind.NetworkEdge, edge.Kind);
        Assert.Equal(DiscoveryMethod.Traceroute, edge.Method);
        Assert.Contains("hop 3", edge.Evidence);
        Assert.Contains("198.51.100.77", edge.Evidence);
        Assert.Equal(3, probe.HighestTtl);   // the walk stopped at the edge; it did not trace to the destination
    }

    [Fact]
    public async Task Edge_search_skips_silent_hops_and_carrier_addresses()
    {
        var probe = new ScriptProbe("192.168.1.1", null, "100.64.0.9", "203.0.113.5");
        var found = await Scanner(new FakeGateways(), probe)
            .FindNetworkEdgeAsync("198.51.100.77", CancellationToken.None);
        Assert.Equal("203.0.113.5", Assert.Single(found).Address);
    }

    [Fact]
    public async Task Edge_search_finds_nothing_when_the_first_public_address_is_the_destination_itself()
    {
        // Only private hops, then the destination answers: no intermediate public hop was seen.
        var probe = new ScriptProbe("192.168.1.1", "10.0.0.1");
        var found = await Scanner(new FakeGateways(), probe)
            .FindNetworkEdgeAsync("198.51.100.77", CancellationToken.None);
        Assert.Empty(found);
    }

    [Fact]
    public async Task Edge_search_with_an_unresolvable_host_throws_for_the_caller_to_report()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Scanner(new FakeGateways())
            .FindNetworkEdgeAsync("definitely-not-a-real-host.invalid", CancellationToken.None));
    }

    [Fact]
    public void ContainerHint_reads_the_dotnet_flag_or_the_docker_marker_file()
    {
        Assert.True(ContainerHint.IsLikelyContainer(k => k == "DOTNET_RUNNING_IN_CONTAINER" ? "true" : null, _ => false));
        Assert.True(ContainerHint.IsLikelyContainer(_ => null, p => p == "/.dockerenv"));
        Assert.False(ContainerHint.IsLikelyContainer(_ => null, _ => false));
        Assert.False(ContainerHint.IsLikelyContainer(k => k == "DOTNET_RUNNING_IN_CONTAINER" ? "false" : null, _ => false));
    }
}
