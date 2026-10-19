using System.Net.Http.Json;
using ConEngine;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>
/// An observer whose Detach throws must not leave the rebuild lock held (every
/// later rebuild, the admin endpoints and shutdown would hang) nor stop the
/// other observers from being detached. Latent today: every real observer only
/// disposes Rx subscriptions.
/// </summary>
public class ObserverDetachTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private static async Task WriteSettingAsync(HttpClient client, string value) =>
        (await client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.WeatherThreshold"] = value })).EnsureSuccessStatusCode();

    /// <summary>Disposes the factory (StopAsync) off the test thread so a hang is a bounded failure.</summary>
    private static async Task<bool> DisposesWithinBoundAsync(ConWatchFactory factory)
    {
        try
        {
            await Task.Run(factory.Dispose).WaitAsync(Bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    [Fact]
    public async Task A_throwing_detach_never_leaves_the_rebuild_lock_held()
    {
        var thrower = new AlwaysThrowDetachObserver();
        var factory = new ConWatchFactory { TestServices = s => s.AddSingleton<IEngineObserver>(thrower) };
        var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.SeedHomeAsync(client);
        await HostTestWait.Eventually(() => Task.FromResult(host.EngineRunning));
        var before = await HostTestWait.SettledRebuildCountAsync(host);

        thrower.Arm();
        await WriteSettingAsync(client, "1");
        await HostTestWait.Eventually(() => Task.FromResult(host.RebuildCount > before && thrower.Calls > 0), 15000);

        // Another write: the rebuild must get through (a held lock hangs it).
        var afterFirst = host.RebuildCount;
        await WriteSettingAsync(client, "3");
        await HostTestWait.Eventually(() => Task.FromResult(host.RebuildCount > afterFirst && host.EngineRunning), 15000);
        var status = await client.GetAsync("/api/v1/status").WaitAsync(Bound);
        Assert.True(status.IsSuccessStatusCode, $"status answered {(int)status.StatusCode}");

        Assert.True(await DisposesWithinBoundAsync(factory), "shutdown hung: the rebuild lock was never released");
    }

    [Fact]
    public async Task A_throwing_detach_does_not_stop_the_other_observers_detaching()
    {
        var thrower = new AlwaysThrowDetachObserver();
        var counter = new DetachCountingObserver();
        // Registered in this order: the thrower comes first in the injected list.
        var factory = new ConWatchFactory
        {
            TestServices = s =>
            {
                s.AddSingleton<IEngineObserver>(thrower);
                s.AddSingleton<IEngineObserver>(counter);
            },
        };
        var client = factory.CreateClient();
        var host = factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.SeedHomeAsync(client);
        await HostTestWait.Eventually(() => Task.FromResult(host.EngineRunning));
        var before = await HostTestWait.SettledRebuildCountAsync(host);
        var detachesBefore = counter.Calls;

        thrower.Arm();
        await WriteSettingAsync(client, "1");
        await HostTestWait.Eventually(() => Task.FromResult(host.RebuildCount > before && thrower.Calls > 0), 15000);

        await HostTestWait.Eventually(() => Task.FromResult(counter.Calls > detachesBefore), 15000);
        await DisposesWithinBoundAsync(factory);
    }
}
