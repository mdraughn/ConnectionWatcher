using System.Text.Json;
using ConPersistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
    /// Changes the rebuild debounce of a running app. Seed with the factory's
    /// instant debounce, settle, then call this: the host reads the value for
    /// every write, so a test gets seeding without delay and the real debounce
    /// (or a long one that only a direct rebuild can pre-empt) afterwards.
    /// </summary>
    public static void SetRebuildDebounce(IServiceProvider services, double seconds) =>
        services.GetRequiredService<IOptions<PersistenceOptions>>().Value.RebuildDebounceSeconds = seconds;

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

    /// <summary>
    /// Every target Responding and the verdict Healthy: nothing left to
    /// transition. Counting rows before this (e.g. once Google alone is up)
    /// races Cloudflare, the pool and the verdict still settling, which then
    /// record transitions after the count.
    /// </summary>
    public static async Task<bool> SteadyStateAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/status");
        if (!response.IsSuccessStatusCode)
            return false;
        var status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var verdict = status.GetProperty("verdict");
        return verdict.ValueKind == JsonValueKind.Object
            && verdict.GetProperty("state").GetString() == "Healthy"
            && status.GetProperty("targets").EnumerateArray()
                .All(t => t.GetProperty("state").GetString() == "Responding");
    }
}
