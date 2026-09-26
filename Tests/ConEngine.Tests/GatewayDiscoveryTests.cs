using System.Net;
using ConInfrastructure.Discovery;

namespace ConEngine.Tests;

public class GatewayDiscoveryTests
{
    [Fact]
    public async Task Results_keep_default_routes_first_then_reachable_then_insertion_order()
    {
        var discovery = new GatewayDiscovery(
            defaultRoutes: () => [IPAddress.Parse("192.168.1.254")],
            probe: (ip, _) => Task.FromResult<long?>(ip.ToString() == "192.168.1.1" ? 3 : null));

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(new[] { "192.168.1.254", "192.168.1.1", "192.168.0.1", "10.0.0.1", "10.0.0.2", "192.168.100.1" },
            result.Select(r => r.Address));
        Assert.Equal("default-route", result[0].Source);
        Assert.False(result[0].Reachable);
        Assert.Null(result[0].RttMs);
        Assert.True(result[1].Reachable);
        Assert.Equal(3, result[1].RttMs);
        Assert.All(result.Skip(1), r => Assert.Equal("probe", r.Source));
    }

    [Fact]
    public async Task Every_candidate_is_probed_at_once()
    {
        var inFlight = 0;
        var peak = 0;
        var gate = new object();
        var discovery = new GatewayDiscovery(
            defaultRoutes: () => [IPAddress.Parse("192.168.1.254")],
            probe: async (_, ct) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                lock (gate) peak = Math.Max(peak, now);
                await Task.Delay(200, ct);
                Interlocked.Decrement(ref inFlight);
                return null;
            });

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(6, result.Count);
        Assert.Equal(6, peak); // one default route + five common candidates, all in flight together
    }

    [Fact]
    public async Task A_default_route_that_is_also_a_common_candidate_is_listed_once_as_default_route()
    {
        var discovery = new GatewayDiscovery(
            defaultRoutes: () => [IPAddress.Parse("192.168.1.1")],
            probe: (_, _) => Task.FromResult<long?>(null));

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(5, result.Count);
        Assert.Equal(("192.168.1.1", "default-route"), (result[0].Address, result[0].Source));
    }
}
