using Xunit.Sdk;

namespace ConWatch.Tests;

/// <summary>Shared waiting helpers for the host tests: wait for a condition with a
/// generous limit, never sleep and hope.</summary>
internal static class HostTestWait
{
    public static async Task Eventually(Func<Task<bool>> condition, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }
        throw new XunitException("Condition not met within timeout");
    }

    /// <summary>
    /// Seeding debounces several rebuilds; a baseline taken while one is still
    /// landing is off by one. Wait until the count has held still for 500 ms
    /// (a fixed sleep was only a guess at that on a busy runner).
    /// </summary>
    public static async Task<int> SettledRebuildCountAsync(WatcherHostService host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var count = host.RebuildCount;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow - stableSince < TimeSpan.FromMilliseconds(500))
        {
            Assert.True(DateTime.UtcNow < deadline, "rebuilds never settled");
            await Task.Delay(50);
            if (host.RebuildCount != count)
            {
                count = host.RebuildCount;
                stableSince = DateTime.UtcNow;
            }
        }
        return count;
    }
}
