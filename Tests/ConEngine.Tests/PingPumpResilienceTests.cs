using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class PingPumpResilienceTests
{
    private static WatcherOptions OneTarget() => new()
    {
        Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
        Targets = [new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Fast" }],
    };

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(20);
        Assert.True(condition(), "Condition not met within timeout");
    }

    [Fact]
    public async Task A_throwing_sample_subscriber_does_not_stop_the_pump()
    {
        var logs = new CapturingLoggerFactory();
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), logs,
            Microsoft.Extensions.Options.Options.Create(OneTarget()));
        int delivered = 0;
        int thrown = 0;
        // Order matters: Rx delivers in subscription order and stops at the first
        // throw, so the counter must be ahead of the thrower to prove the pump
        // kept going AFTER a throw.
        engine.PingSampleSource.Subscribe(_ => Interlocked.Increment(ref delivered));
        engine.PingSampleSource.Subscribe(_ =>
        {
            Interlocked.Increment(ref thrown);
            throw new InvalidOperationException("observer bug");
        });
        Assert.Null(engine.LastResultUtc);
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            await Eventually(() => Volatile.Read(ref thrown) >= 1);
            var afterFirstThrow = Volatile.Read(ref delivered);
            await Eventually(() => Volatile.Read(ref delivered) >= afterFirstThrow + 5);
            Assert.NotNull(engine.LastResultUtc);
            Assert.True(engine.LastResultUtc >= engine.StartedUtc);
            // Still pumping: a newer result arrives. Waited for, not slept for —
            // a fixed 200 ms window can pass with nothing processed on a busy
            // 2-core CI runner.
            var stampAfterThrow = engine.LastResultUtc;
            await Eventually(() => engine.LastResultUtc > stampAfterThrow);
            // Several samples threw; the repeat is throttled to one line.
            Assert.True(Volatile.Read(ref thrown) >= 2);
            lock (logs.Entries)
                Assert.Single(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("subscriber threw"));
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    /// <summary>
    /// A subscriber's own cancellation (its HTTP call timed out, say) is not
    /// the pump's cancellation: nothing has been asked to stop, so the pump
    /// must treat it like any other subscriber bug and keep going.
    /// </summary>
    [Fact]
    public async Task A_subscriber_throwing_a_foreign_cancellation_does_not_stop_the_pump()
    {
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(OneTarget()));
        int delivered = 0;
        int thrown = 0;
        engine.PingSampleSource.Subscribe(_ => Interlocked.Increment(ref delivered));
        engine.PingSampleSource.Subscribe(_ =>
        {
            Interlocked.Increment(ref thrown);
            throw new OperationCanceledException("subscriber's own timeout");
        });
        Assert.Null(engine.LastResultUtc);
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            await Eventually(() => Volatile.Read(ref thrown) >= 1);
            var afterFirstThrow = Volatile.Read(ref delivered);
            await Eventually(() => Volatile.Read(ref delivered) >= afterFirstThrow + 5);
            Assert.NotNull(engine.LastResultUtc);
            Assert.True(engine.LastResultUtc >= engine.StartedUtc);
            // Still pumping: a newer result arrives. Waited for, not slept for —
            // a fixed 200 ms window can pass with nothing processed on a busy
            // 2-core CI runner.
            var stampAfterThrow = engine.LastResultUtc;
            await Eventually(() => engine.LastResultUtc > stampAfterThrow);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Stall_threshold_follows_the_slowest_profile()
    {
        var options = OneTarget();
        options.Profiles["Fast"].PingTimeoutSeconds = 7;
        options.Profiles["Slow"] = new PingProfileConfig { NormalIntervalSeconds = 100, PingTimeoutSeconds = 5 };
        options.Targets.Add(new TargetConfig { Name = "Far", Address = "203.0.113.9", Profile = "Slow" });
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(307), engine.StallAfter);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
