using System.Diagnostics;
using System.Net;
using ConCore;
using ConEngine.Network;
using ConEngine.Scheduling;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

/// <summary>
/// The pump's own per-message guard (review item E1), the engine's stop contract,
/// and the stop grace. PingPumpResilienceTests throws from the engine's sample
/// stream, where the engine's own catch fires first and the pump's never runs;
/// these throw from <see cref="PingManager.PingResultSource"/> directly.
/// </summary>
public class PingManagerGuardTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.0.2.1");

    private static PingManager NewManager(IPingTransceiverFactory factory, CancellationToken ct) =>
        new(factory, ct, NullLoggerFactory.Instance, TimeProvider.System);

    private static void AddController(PingManager manager, int intervalMs = 10, int timeoutMs = 1000) =>
        manager.GetOrCreateController(Guid.NewGuid(), Ip, ProbeSpec.Icmp,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(intervalMs), TimeProvider.System),
            () => timeoutMs);

    public static TheoryData<bool> ThrowKinds => new() { false, true };

    private static Exception Bug(bool foreignCancellation) => foreignCancellation
        ? new OperationCanceledException("subscriber's own timeout")
        : new InvalidOperationException("observer bug");

    [Theory]
    [MemberData(nameof(ThrowKinds))]
    public async Task A_throwing_result_subscriber_does_not_end_the_pump(bool foreignCancellation)
    {
        using var cts = new CancellationTokenSource();
        var manager = NewManager(new FakePingTransceiverFactory(), cts.Token);
        int delivered = 0, thrown = 0;
        // Counter first: Rx stops at the first throwing observer, so the counter
        // must be ahead of the thrower to show messages arriving after a throw.
        manager.PingResultSource.Subscribe(_ => Interlocked.Increment(ref delivered));
        manager.PingResultSource.Subscribe(_ =>
        {
            Interlocked.Increment(ref thrown);
            throw Bug(foreignCancellation);
        });
        try
        {
            AddController(manager);
            await TestWait.Eventually(() => Volatile.Read(ref thrown) >= 1, what: "no subscriber ever threw");
            var afterFirstThrow = Volatile.Read(ref delivered);
            await TestWait.Eventually(() => Volatile.Read(ref delivered) >= afterFirstThrow + 5,
                what: "the pump stopped delivering after a subscriber threw");
        }
        finally
        {
            await manager.StopAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task A_throw_does_not_advance_the_last_result_stamp()
    {
        using var cts = new CancellationTokenSource();
        var manager = NewManager(new FakePingTransceiverFactory(), cts.Token);
        int thrown = 0;
        manager.PingResultSource.Subscribe(_ =>
        {
            Interlocked.Increment(ref thrown);
            throw new InvalidOperationException("observer bug");
        });
        Assert.Null(manager.LastResultUtc);
        try
        {
            AddController(manager);
            // The pump is alive and throwing (5 throws), and the stamp, which means
            // "last result fully processed", has not moved.
            await TestWait.Eventually(() => Volatile.Read(ref thrown) >= 5, what: "fewer than 5 throws");
            Assert.Null(manager.LastResultUtc);
        }
        finally
        {
            await manager.StopAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task A_throwing_transition_observer_does_not_stop_the_engine()
    {
        await using var h = EngineHarness.Home();
        await h.StartAsync();
        await h.WaitStateAsync("Router", HostState.Responding);

        int calls = 0;
        using var sub = h.Engine.TransitionSource.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("transition observer bug");
        });
        // Force a Router transition now that the thrower is listening.
        h.Pings.SetUp(h.Ip("Router"), false);
        await TestWait.Eventually(() => Volatile.Read(ref calls) >= 1, what: "the observer never saw a transition");

        var router = h.Ip("Router");
        var pingsAfterThrow = h.Pings.PingCount(router);
        var stampAfterThrow = h.Engine.LastResultUtc;
        await TestWait.Eventually(() => h.Pings.PingCount(router) >= pingsAfterThrow + 5,
            what: "Router stopped being pinged after the observer threw");
        await TestWait.Eventually(() => h.Engine.LastResultUtc > stampAfterThrow,
            what: "the result pump stopped stamping after the observer threw");
    }

    [Fact]
    public async Task Stop_delivers_nothing_afterwards_and_is_idempotent()
    {
        await using var h = EngineHarness.Home();
        int samples = 0, transitions = 0;
        await h.StartAsync();
        using var s1 = h.Engine.PingSampleSource.Subscribe(_ => Interlocked.Increment(ref samples));
        using var s2 = h.Engine.TransitionSource.Subscribe(_ => Interlocked.Increment(ref transitions));
        // Positive condition first: the engine was delivering before the stop.
        await TestWait.Eventually(() => Volatile.Read(ref samples) >= 10, what: "no samples before stop");
        h.Pings.SetUp(h.Ip("Router"), false); // keep transitions flowing up to the stop

        await h.Engine.StopAsync();
        int samplesAtStop = Volatile.Read(ref samples), transitionsAtStop = Volatile.Read(ref transitions);

        await Task.Delay(500); // absence window: allowed only after the positive condition above
        Assert.Equal(samplesAtStop, Volatile.Read(ref samples));
        Assert.Equal(transitionsAtStop, Volatile.Read(ref transitions));

        await h.Engine.StopAsync(); // second stop: no throw

        var neverStarted = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(new ConEngine.Configuration.WatcherOptions()));
        await neverStarted.StopAsync(); // never started: no throw
    }

    /// <summary>A probe that ignores its token and waits on a gate the test controls.</summary>
    private sealed class StubbornFactory(TaskCompletionSource gate) : IPingTransceiverFactory
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPingTransceiver Create(IPAddress ip, ProbeSpec probe, CancellationToken ct) => new Probe(ip, this, gate);

        private sealed class Probe(IPAddress ip, StubbornFactory owner, TaskCompletionSource gate) : IPingTransceiver
        {
            public IPAddress IpAddress => ip;

            public async Task<PingResult> PingAsync(int timeoutMs)
            {
                owner.Entered.TrySetResult();
                await gate.Task; // ignores cancellation on purpose
                return new PingResult(ip, false, 0, "released");
            }

            public void Dispose() { }
        }
    }

    [Fact]
    public async Task Stop_is_bounded_by_the_grace_when_a_probe_ignores_cancellation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new StubbornFactory(gate);
        using var cts = new CancellationTokenSource();
        var manager = NewManager(factory, cts.Token);
        try
        {
            AddController(manager, intervalMs: 1, timeoutMs: 60_000);
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var sw = Stopwatch.StartNew();
            // WaitAsync keeps a regression (an unbounded wait) a failure, not a hung run.
            await manager.StopAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        }
        finally
        {
            gate.TrySetResult(); // let the stuck probe finish so nothing leaks past the test
            cts.Cancel();
        }
    }
}
