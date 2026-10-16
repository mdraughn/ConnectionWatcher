using ConCore.Logging;

namespace ConEngine.Tests;

public class LogThrottleTests
{
    [Fact]
    public void Logs_the_first_then_once_per_window_with_the_suppressed_count() // Review Focus 5
    {
        var t0 = DateTimeOffset.Parse("2026-10-16T00:00:00Z");
        var throttle = new LogThrottle(TimeSpan.FromSeconds(60));
        Assert.True(throttle.ShouldLog(t0, out var n)); Assert.Equal(0, n);
        Assert.False(throttle.ShouldLog(t0.AddSeconds(1), out _));
        Assert.False(throttle.ShouldLog(t0.AddSeconds(59), out _));
        Assert.True(throttle.ShouldLog(t0.AddSeconds(60), out n)); Assert.Equal(2, n);
        Assert.False(throttle.ShouldLog(t0.AddSeconds(61), out _));
        Assert.True(throttle.ShouldLog(t0.AddSeconds(200), out n)); Assert.Equal(1, n);
    }
}
