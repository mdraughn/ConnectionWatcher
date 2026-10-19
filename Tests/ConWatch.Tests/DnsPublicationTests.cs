using System.Text.Json;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
using ConNotify;

namespace ConWatch.Tests;

/// <summary>The "Name resolution" entity's availability, state and attributes follow the engine's resolver monitors.</summary>
public class DnsPublicationTests
{
    [Fact]
    public async Task Dns_publication_follows_the_engine()
    {
        await using var h = EngineHarness.Home();
        await h.StartAsync();

        await h.WaitStateAsync("Resolver", HostState.Responding);
        var up = DnsPublication.From(h.Engine);
        Assert.Equal("online", up.Availability);
        Assert.Equal("ON", up.State);
        var attributes = JsonSerializer.Deserialize<JsonElement>(up.Attributes);
        var resolver = Assert.Single(attributes.GetProperty("systemResolvers").EnumerateArray());
        Assert.Equal("Resolver", resolver.GetProperty("name").GetString());
        Assert.Equal("192.0.2.53", resolver.GetProperty("address").GetString());
        Assert.Equal("Responding", resolver.GetProperty("state").GetString());

        h.Pings.SetUp(h.Ip("Resolver"), false);
        await h.WaitStateAsync("Resolver", HostState.Down);
        var down = DnsPublication.From(h.Engine);
        Assert.Equal("online", down.Availability);
        Assert.Equal("OFF", down.State);
    }

    [Fact]
    public async Task A_switched_off_dns_watch_reads_offline_not_down()
    {
        await using var h = EngineHarness.Home(o => o.Verdict.DnsWatch = false);
        await h.StartAsync();
        await h.WaitStateAsync("Router", HostState.Responding);

        Assert.Equal("offline", DnsPublication.From(h.Engine).Availability);
    }
}
