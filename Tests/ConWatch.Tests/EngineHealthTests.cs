using System.Net;
using ConCore;
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

    /// <summary>A transceiver whose ping never completes unless cancelled — so
    /// no result can be produced however the fake clock is advanced.</summary>
    private sealed class HangingFactory : IPingTransceiverFactory
    {
        public IPingTransceiver Create(IPAddress ip, ProbeSpec probe, CancellationToken ct) => new Hanging(ip, ct);

        private sealed class Hanging(IPAddress ip, CancellationToken ct) : IPingTransceiver
        {
            public IPAddress IpAddress => ip;

            public async Task<PingResult> PingAsync(int timeoutMs)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new PingResult(ip, false, 0, "unreachable");
            }

            public void Dispose() { }
        }
    }

    /// <summary>The engine started, with an active monitor, and physically
    /// unable to produce a result: the clean way to get "started, active,
    /// silent". A completing fake transceiver would not do — advancing the
    /// fake clock fires the controller's cadence timer synchronously, and a
    /// result could be pumped in before Classify runs.</summary>
    private static async Task<(ConEngine.WatcherEngine Engine, FakeTimeProvider Time, CancellationTokenSource Cts)> SilentEngineAsync()
    {
        var time = new FakeTimeProvider(T0);
        var engine = new ConEngine.WatcherEngine(time, new HangingFactory(),
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
    public void Validation_failure_with_no_engine_is_ConfigInvalid()
    {
        Assert.Equal("ConfigInvalid", EngineHealth.Classify(null, enabled: true, T0,
            "Profile 'X': FalteringThreshold (0.75) must be below RespondingThreshold (0.5)").Status);
        // No recorded failure: an engine-less node is just down.
        Assert.Equal("EngineDown", EngineHealth.Classify(null, enabled: true, T0, null).Status);
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
