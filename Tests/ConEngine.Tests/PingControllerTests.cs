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

    private static async Task<List<RoutedPingResult>> Collect(ChannelReader<RoutedPingResult> reader, int count, int timeoutMs = 5000)
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
        using var controller = new PingController(factory, NullLogger.Instance);

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

        using var controllerA = new PingController(factory, NullLogger.Instance);
        using var controllerB = new PingController(factory, NullLogger.Instance);
        _ = controllerA.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp, cadence, () => 1000, cts.Token);
        _ = controllerB.Launch(Guid.NewGuid(), IpB, channel.Writer, ProbeSpec.Icmp, cadence, () => 1000, cts.Token);

        await Collect(channel.Reader, 2);
        controllerA.Stop();
        await Task.Delay(100);

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
        using var controller = new PingController(factory, NullLogger.Instance);

        bool bursting = false;
        _ = controller.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System),
            () => bursting ? 1000 : 3000, cts.Token);

        await Collect(channel.Reader, 2);
        bursting = true;
        await Collect(channel.Reader, 3);
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
        using var controller = new PingController(factory, NullLogger.Instance);

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
}
