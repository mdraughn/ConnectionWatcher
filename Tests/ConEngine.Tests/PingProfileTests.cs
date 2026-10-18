using ConCore.Targets;
using ConEngine.Configuration;

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

    [Fact]
    public void Local_preset_and_record_defaults_are_pinned()
    {
        var local = PingProfile.Local;
        Assert.Equal("Local", local.Name);
        Assert.Equal(PingProfile.Public.NormalInterval, local.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), local.PingTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), local.BurstInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), local.BurstMaxDuration);
        Assert.Equal(30, local.HistorySize);
        Assert.Equal(6, local.MinUsableSample);
        Assert.Equal(0.80, local.RespondingThreshold);
        Assert.Equal(0.50, local.FalteringThreshold);
        Assert.Equal(0.10, local.DownThreshold);
        Assert.Equal(0.25, local.RecoveringThreshold);

        var d = new PingProfile { Name = "x" };
        Assert.Equal(TimeSpan.FromSeconds(10), d.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), d.PingTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), d.BurstInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), d.BurstMaxDuration);
        Assert.Equal(100, d.HistorySize);
        Assert.Equal(10, d.MinUsableSample);
        Assert.Equal(0.95, d.RespondingThreshold);
        Assert.Equal(0.75, d.FalteringThreshold);
        Assert.Equal(0.10, d.DownThreshold);
        Assert.Equal(0.25, d.RecoveringThreshold);
    }

    [Fact]
    public void PingProfileConfig_defaults_equal_PingProfile_defaults()
    {
        Assert.Equal(new PingProfile { Name = "x" }, new PingProfileConfig().ToProfile("x"));
    }

    [Fact]
    public void ToProfile_maps_every_field()
    {
        var config = new PingProfileConfig
        {
            NormalIntervalSeconds = 11,
            PingTimeoutSeconds = 4,
            BurstIntervalSeconds = 2,
            BurstMaxDurationSeconds = 45,
            HistorySize = 55,
            MinUsableSample = 7,
            RespondingThreshold = 0.91,
            FalteringThreshold = 0.62,
            DownThreshold = 0.13,
            RecoveringThreshold = 0.37,
        };

        var p = config.ToProfile("custom");

        Assert.Equal("custom", p.Name);
        Assert.Equal(TimeSpan.FromSeconds(11), p.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(4), p.PingTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), p.BurstInterval);
        Assert.Equal(TimeSpan.FromSeconds(45), p.BurstMaxDuration);
        Assert.Equal(55, p.HistorySize);
        Assert.Equal(7, p.MinUsableSample);
        Assert.Equal(0.91, p.RespondingThreshold);
        Assert.Equal(0.62, p.FalteringThreshold);
        Assert.Equal(0.13, p.DownThreshold);
        Assert.Equal(0.37, p.RecoveringThreshold);
    }
}
