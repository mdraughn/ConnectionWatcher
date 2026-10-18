using System.Net;
using System.Threading.Channels;
using ConCore;
using ConEngine.Network;
using ConEngine.Scheduling;
using ConEngine.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class PingControllerTests
{
    private static readonly IPAddress IpA = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress IpB = IPAddress.Parse("10.0.0.2");

    private static async Task<List<RoutedPingResult>> Collect(ChannelReader<RoutedPingResult> reader, int count, int timeoutMs = 15000)
    {
        var results = new List<RoutedPingResult>();
        using var cts = new CancellationTokenSource(timeoutMs);
        while (results.Count < count)
            results.Add(await reader.ReadAsync(cts.Token));
        return results;
    }

    [Fact]
    public async Task Controller_pings_at_cadence_and_reports_results()
    {
        var factory = new FakePingTransceiverFactory();
        var channel = Channel.CreateUnbounded<RoutedPingResult>();
        using var cts = new CancellationTokenSource();
        using var controller = new PingController(factory, NullLogger.Instance, TimeProvider.System);

        _ = controller.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System),
            () => 1000, cts.Token);

        var results = await Collect(channel.Reader, 3);
        Assert.All(results, r => Assert.True(r.Result.Success));
        cts.Cancel();
    }

    [Fact]
    public async Task Stopping_one_controller_does_not_complete_shared_channel()
    {
        var factory = new FakePingTransceiverFactory();
        var channel = Channel.CreateUnbounded<RoutedPingResult>();
        using var cts = new CancellationTokenSource();
        var cadence = new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System);

        using var controllerA = new PingController(factory, NullLogger.Instance, TimeProvider.System);
        using var controllerB = new PingController(factory, NullLogger.Instance, TimeProvider.System);
        _ = controllerA.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp, cadence, () => 1000, cts.Token);
        _ = controllerB.Launch(Guid.NewGuid(), IpB, channel.Writer, ProbeSpec.Icmp, cadence, () => 1000, cts.Token);

        await Collect(channel.Reader, 2);
        await controllerA.StopAsync(TimeSpan.FromSeconds(1));

        // Channel must still be open and still carrying B's results.
        Assert.False(channel.Reader.Completion.IsCompleted);
        var afterStop = await Collect(channel.Reader, 3);
        Assert.All(afterStop.Where(r => !r.Result.IpAddress.Equals(IpA)), r => Assert.True(r.Result.Success));
        cts.Cancel();
    }

    [Fact]
    public async Task Timeout_provider_is_read_per_ping()
    {
        var factory = new FakePingTransceiverFactory();
        var channel = Channel.CreateUnbounded<RoutedPingResult>();
        using var cts = new CancellationTokenSource();
        using var controller = new PingController(factory, NullLogger.Instance, TimeProvider.System);

        var bursting = false; // written here, read on the ping loop's thread
        _ = controller.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System),
            () => Volatile.Read(ref bursting) ? 1000 : 3000, cts.Token);

        await Collect(channel.Reader, 2);
        Volatile.Write(ref bursting, true);
        // Wait for the condition (a ping observed with the burst timeout), not a count.
        await TestWait.Eventually(() => factory.ObservedTimeouts[IpA].Contains(1000), 10_000,
            "no ping ever used the burst timeout");
        cts.Cancel();

        var seen = factory.ObservedTimeouts[IpA].ToArray();
        Assert.Contains(3000, seen);
        Assert.Contains(1000, seen);
        Assert.Equal(3000, seen[0]); // steady first, burst later
    }

    [Fact]
    public async Task Transceiver_exception_reports_failure_and_loop_survives()
    {
        var factory = new FakePingTransceiverFactory();
        factory.SetThrow(IpA, true);
        var channel = Channel.CreateUnbounded<RoutedPingResult>();
        using var cts = new CancellationTokenSource();
        using var controller = new PingController(factory, NullLogger.Instance, TimeProvider.System);

        _ = controller.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System),
            () => 1000, cts.Token);

        var failures = await Collect(channel.Reader, 2);
        Assert.All(failures, r => Assert.False(r.Result.Success));

        // Heal the transceiver: the same loop must recover and report successes.
        factory.SetThrow(IpA, false);
        using var cts2 = new CancellationTokenSource(5000);
        RoutedPingResult? healed = null;
        while (healed is not { Result.Success: true })
            healed = await channel.Reader.ReadAsync(cts2.Token);
        Assert.True(healed.Result.Success);
        cts.Cancel();
    }

    /// <summary>The OLD production transceiver behavior: cancellation comes back
    /// as a failed sample. The controller must still not forward it.</summary>
    private sealed class CancelReportingFactory : IPingTransceiverFactory
    {
        /// <summary>Completed when a probe has entered its (hanging) ping.</summary>
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPingTransceiver Create(IPAddress ip, ProbeSpec probe, CancellationToken ct) => new Probe(ip, ct, Entered);

        private sealed class Probe(IPAddress ip, CancellationToken ct, TaskCompletionSource entered) : IPingTransceiver
        {
            public IPAddress IpAddress => ip;

            public async Task<PingResult> PingAsync(int timeoutMs)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                return new PingResult(ip, false, 0, "Cancelled");
            }

            public void Dispose() { }
        }
    }

    [Fact]
    public async Task A_probe_cancelled_mid_flight_writes_nothing_to_the_channel()
    {
        var factory = new CancelReportingFactory();
        var channel = Channel.CreateUnbounded<RoutedPingResult>();
        using var cts = new CancellationTokenSource();
        using var controller = new PingController(factory, NullLogger.Instance, TimeProvider.System);

        _ = controller.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(1), TimeProvider.System),
            () => 60_000, cts.Token);
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the loop is inside its first (hanging) probe

        await controller.StopAsync(TimeSpan.FromSeconds(2));

        Assert.False(channel.Reader.TryRead(out _), "a cancelled probe must not become a lost sample");
    }

    [Fact]
    public async Task Results_carry_the_engine_clock_not_the_wall_clock()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var factory = new FakePingTransceiverFactory();
        var channel = Channel.CreateUnbounded<RoutedPingResult>();
        using var cts = new CancellationTokenSource();
        using var controller = new PingController(factory, NullLogger.Instance, time);

        _ = controller.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System),
            () => 1000, cts.Token);

        var result = (await Collect(channel.Reader, 1)).Single();
        cts.Cancel();
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z"), result.Result.Timestamp);
    }
}
