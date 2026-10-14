using ConCore.Targets;

namespace ConEngine.Tests;

public class PingProfileTests
{
    [Fact]
    public void Internal_preset_pings_fast()
    {
        Assert.Equal("Internal", PingProfile.Internal.Name);
        Assert.Equal(TimeSpan.FromSeconds(1), PingProfile.Internal.NormalInterval);
    }

    [Fact]
    public void Path_preset_is_steady_but_not_lan_fast()
    {
        Assert.Equal("Path", PingProfile.Path.Name);
        Assert.Equal(TimeSpan.FromSeconds(5), PingProfile.Path.NormalInterval);
    }

    [Fact]
    public void Public_preset_pings_politely()
    {
        Assert.Equal("Public", PingProfile.Public.Name);
        Assert.Equal(TimeSpan.FromSeconds(10), PingProfile.Public.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), PingProfile.Public.BurstInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), PingProfile.Public.BurstMaxDuration);
    }

    [Fact]
    public void BuiltIns_lists_the_five_presets_in_order()
    {
        Assert.Equal(["Internal", "Path", "Public", "Local", "Resolver"], PingProfile.BuiltIns.Select(p => p.Name));
    }

    [Fact]
    public void Resolver_preset_queries_every_half_minute_and_bursts_on_a_miss()
    {
        var p = PingProfile.Resolver;
        Assert.Equal("Resolver", p.Name);
        Assert.Equal(TimeSpan.FromSeconds(30), p.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), p.PingTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), p.BurstInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), p.BurstMaxDuration);
        Assert.Equal(30, p.HistorySize);
        Assert.Equal(6, p.MinUsableSample);
        Assert.Equal(0.80, p.RespondingThreshold);
        Assert.Equal(0.50, p.FalteringThreshold);
        Assert.Equal(0.10, p.DownThreshold);
        Assert.Equal(0.25, p.RecoveringThreshold);
        Assert.Contains(PingProfile.BuiltIns, b => ReferenceEquals(b, p));
        Assert.True(PingProfile.IsBuiltIn("resolver"));
    }
}
