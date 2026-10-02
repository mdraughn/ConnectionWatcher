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
    public void BuiltIns_lists_the_four_presets_in_order()
    {
        Assert.Equal(["Internal", "Path", "Public", "Local"], PingProfile.BuiltIns.Select(p => p.Name));
    }
}
