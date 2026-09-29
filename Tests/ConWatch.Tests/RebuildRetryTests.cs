using System.Net.Http.Json;
using ConEngine;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>
/// A rebuild that throws (a transient database error, say) used to leave the
/// node engine-less until the next config write or a restart — silently not
/// monitoring. The host now retries it on its own.
/// </summary>
public class RebuildRetryTests : IDisposable
{
    /// <summary>Throws from Attach once, when armed: the rebuild's start fails like any transient error.</summary>
    private sealed class FailOnceObserver : IEngineObserver
    {
        private int _fired;
        public volatile bool Armed;
        public bool Fired => Volatile.Read(ref _fired) == 1;

        public void Attach(WatcherEngine engine)
        {
            if (Armed && Interlocked.Exchange(ref _fired, 1) == 0)
                throw new InvalidOperationException("injected rebuild failure");
        }

        public void Detach() { }
    }

    private readonly FailOnceObserver _failOnce = new();
    private readonly ConWatchFactory _factory;
    private readonly HttpClient _client;

    public RebuildRetryTests()
    {
        _factory = new ConWatchFactory { TestServices = s => s.AddSingleton<IEngineObserver>(_failOnce) };
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_failed_rebuild_is_retried_without_another_config_write()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(() => Task.FromResult(host.EngineRunning));

        _failOnce.Armed = true;
        (await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.WeatherThreshold"] = "1" })).EnsureSuccessStatusCode();
        await ConfigApiTests.Eventually(() => Task.FromResult(_failOnce.Fired));

        // No further writes: only the host itself can bring the engine back.
        await ConfigApiTests.Eventually(() => Task.FromResult(host.EngineRunning), timeoutMs: 15000);
        var status = await _client.GetAsync("/api/v1/status");
        Assert.True(status.IsSuccessStatusCode, $"status answered {(int)status.StatusCode}");
    }
}
