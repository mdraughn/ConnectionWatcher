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
    public void External_preset_pings_politely()
    {
        Assert.Equal("External", PingProfile.External.Name);
        Assert.Equal(TimeSpan.FromSeconds(10), PingProfile.External.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), PingProfile.External.BurstInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), PingProfile.External.BurstMaxDuration);
    }
}
