using System.Net.Http.Json;
using ConEngine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConWatch.Tests;

/// <summary>
/// A rebuild that throws (a transient database error, say) used to leave the
/// node engine-less until the next config write or a restart: silently not
/// monitoring. The host now retries it on its own, on an escalating schedule.
///
/// Every test that shortens <see cref="WatcherHostService.RetryDelays"/> lives
/// in THIS class: xUnit runs the tests of one class one after another, and
/// <see cref="Dispose"/> puts the default schedule back, so the static never
/// holds a shortened schedule while another test of this class runs. Failed
/// rebuilds in other classes only ever read the delay, and a sooner retry is
/// harmless to them.
/// </summary>
public class RebuildRetryTests : IDisposable
{
    private static readonly TimeSpan[] DefaultDelays = WatcherHostService.RetryDelays;

    private readonly AttachBombObserver _bomb = new();
    private readonly CapturingLoggerProvider _log = new();
    private readonly ConWatchFactory _factory;
    private readonly HttpClient _client;
    private readonly WatcherHostService _host;

    public RebuildRetryTests()
    {
        _factory = new ConWatchFactory
        {
            TestServices = s =>
            {
                s.AddSingleton<IEngineObserver>(_bomb);
                s.AddLogging(b => b.AddProvider(_log).AddFilter<CapturingLoggerProvider>(null, LogLevel.Debug));
            },
        };
        _client = _factory.CreateClient();
        _host = _factory.Services.GetRequiredService<WatcherHostService>();
    }

    public void Dispose()
    {
        WatcherHostService.RetryDelays = DefaultDelays;
        _factory.Dispose();
    }

    /// <summary>Seeds the Home config and waits until the seeding rebuilds are
    /// done, so no queued rebuild can bring a failed engine back for us.</summary>
    private async Task<int> SeedAndSettleAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await HostTestWait.Eventually(() => Task.FromResult(_host.EngineRunning));
        return await HostTestWait.SettledRebuildCountAsync(_host);
    }

    private async Task WriteSettingAsync(string value) =>
        (await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.WeatherThreshold"] = value })).EnsureSuccessStatusCode();

    [Fact]
    public async Task A_failed_rebuild_is_retried_without_another_config_write()
    {
        var before = await SeedAndSettleAsync();

        _bomb.Arm(1);
        await WriteSettingAsync("1");
        await HostTestWait.Eventually(() => Task.FromResult(_bomb.Fired == 1));

        // No further writes: only the host's own retry can bring the engine back.
        await HostTestWait.Eventually(() => Task.FromResult(_host.EngineRunning && _host.RebuildCount >= before + 2),
            timeoutMs: 15000);
        // The failed rebuild plus its retry; nothing else rebuilt.
        Assert.Equal(before + 2, await HostTestWait.SettledRebuildCountAsync(_host));
        var status = await _client.GetAsync("/api/v1/status");
        Assert.True(status.IsSuccessStatusCode, $"status answered {(int)status.StatusCode}");
    }

    [Fact]
    public async Task Failures_escalate_through_the_schedule()
    {
        WatcherHostService.RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(2)];
        var before = await SeedAndSettleAsync();

        _bomb.Arm(2);
        await WriteSettingAsync("1");
        await HostTestWait.Eventually(() => Task.FromResult(_bomb.Fired >= 1));

        // Absence window: the second retry waits 800 ms after the second
        // failure, so the engine cannot be back this soon. A schedule that
        // kept using the first delay would have it back in about 400 ms.
        await Task.Delay(500);
        Assert.False(_host.EngineRunning, "the engine came back before the second delay could elapse");

        await HostTestWait.Eventually(() => Task.FromResult(_host.EngineRunning), timeoutMs: 10000);
        Assert.Equal(2, _bomb.Fired);
        // The write's own rebuild and two retries.
        Assert.Equal(before + 3, await HostTestWait.SettledRebuildCountAsync(_host));
    }

    [Fact]
    public async Task A_later_good_rebuild_cancels_the_pending_retry()
    {
        WatcherHostService.RetryDelays = [TimeSpan.FromMilliseconds(300)];
        var before = await SeedAndSettleAsync();

        _bomb.Arm(1);
        await WriteSettingAsync("1");
        await HostTestWait.Eventually(() => Task.FromResult(_bomb.Fired == 1));
        // A valid write straight away: its rebuild beats the retry timer.
        await WriteSettingAsync("3");
        await HostTestWait.Eventually(() => Task.FromResult(_host.EngineRunning && _host.RebuildCount >= before + 2));

        // Absence window, entered after the positive condition: well past the
        // 300 ms the retry would have fired at.
        await Task.Delay(1200);
        Assert.Equal(before + 2, await HostTestWait.SettledRebuildCountAsync(_host));
        Assert.True(_host.EngineRunning);
    }

    [Fact]
    public async Task No_retry_after_stop()
    {
        WatcherHostService.RetryDelays = [TimeSpan.FromMilliseconds(600)];
        await SeedAndSettleAsync();

        _bomb.Arm(1);
        await WriteSettingAsync("1");
        // The failure is logged right before the retry is scheduled.
        await HostTestWait.Eventually(() => Task.FromResult(
            _log.Snapshot().Any(e => e.Message.Contains("Engine rebuild failed"))));

        _factory.Dispose();
        var atStop = _log.Snapshot().Count(e => e.Category.EndsWith(nameof(WatcherHostService)));

        // Absence window: well past the 600 ms retry delay.
        await Task.Delay(1500);
        var after = _log.Snapshot().Where(e => e.Category.EndsWith(nameof(WatcherHostService))).Skip(atStop).ToList();
        Assert.True(after.Count == 0,
            "the host logged after stop: " + string.Join(" | ", after.Select(e => e.Message)));
    }
}
