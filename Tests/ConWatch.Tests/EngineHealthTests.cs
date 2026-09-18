using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConWatch.Tests;

public class EngineHealthTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-18T12:00:00Z");

    private static WatcherOptions OneTarget() => new()
    {
        Profiles = { ["P"] = new PingProfileConfig { NormalIntervalSeconds = 10, PingTimeoutSeconds = 3 } },
        Targets = [new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "P" }],
    };

    /// <summary>A fake clock keeps every controller asleep, so the engine never
    /// produces a result: a clean way to get "started, active, silent".</summary>
    private static async Task<(ConEngine.WatcherEngine Engine, FakeTimeProvider Time, CancellationTokenSource Cts)> SilentEngineAsync()
    {
        var time = new FakeTimeProvider(T0);
        var engine = new ConEngine.WatcherEngine(time, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(OneTarget()));
        var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        return (engine, time, cts);
    }

    [Fact]
    public void No_engine_is_EngineDown_when_enabled_and_Disabled_otherwise()
    {
        Assert.Equal("EngineDown", EngineHealth.Classify(null, enabled: true, T0).Status);
        Assert.Equal("Disabled", EngineHealth.Classify(null, enabled: false, T0).Status);
    }

    [Fact]
    public async Task Fresh_engine_is_healthy_until_the_stall_window_passes_with_no_result()
    {
        var (engine, time, cts) = await SilentEngineAsync();
        try
        {
            Assert.Equal("Healthy", EngineHealth.Classify(engine, true, time.GetUtcNow()).Status);

            time.Advance(engine.StallAfter + TimeSpan.FromSeconds(1)); // 3*10 + 3 = 33 s window
            var verdict = EngineHealth.Classify(engine, true, time.GetUtcNow());
            Assert.Equal("EngineStalled", verdict.Status);
            Assert.True(verdict.Stalled);
            Assert.Equal(TimeSpan.FromSeconds(34), verdict.SilenceFor);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Engine_with_no_monitors_never_stalls()
    {
        var time = new FakeTimeProvider(T0);
        var engine = new ConEngine.WatcherEngine(time, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(new WatcherOptions()));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            time.Advance(TimeSpan.FromHours(1));
            Assert.Equal("Healthy", EngineHealth.Classify(engine, true, time.GetUtcNow()).Status);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
