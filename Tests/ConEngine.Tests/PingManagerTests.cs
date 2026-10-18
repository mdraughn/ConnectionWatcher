using System.Diagnostics;
using System.Net;
using ConCore;
using ConEngine.Network;
using ConEngine.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class PingManagerTests
{
    /// <summary>A transceiver whose ping never completes unless cancelled.</summary>
    private sealed class HangingFactory(int expected) : IPingTransceiverFactory
    {
        private int _entered;
        private readonly int _expected = expected;

        /// <summary>Completed once <c>expected</c> probes are inside their hanging ping.</summary>
        public readonly TaskCompletionSource AllEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPingTransceiver Create(IPAddress ip, ProbeSpec probe, CancellationToken ct) => new Hanging(ip, ct, this);

        private sealed class Hanging(IPAddress ip, CancellationToken ct, HangingFactory owner) : IPingTransceiver
        {
            public IPAddress IpAddress => ip;

            public async Task<PingResult> PingAsync(int timeoutMs)
            {
                if (Interlocked.Increment(ref owner._entered) >= owner._expected)
                    owner.AllEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new PingResult(ip, false, 0, "unreachable");
            }

            public void Dispose() { }
        }
    }

    [Fact]
    public async Task StopAsync_with_ten_hanging_controllers_finishes_within_the_grace_period()
    {
        using var cts = new CancellationTokenSource();
        var factory = new HangingFactory(10);
        var manager = new PingManager(factory, cts.Token, NullLoggerFactory.Instance, TimeProvider.System);
        var cadence = new FixedIntervalCadence(TimeSpan.FromMilliseconds(1), TimeProvider.System);
        for (int i = 1; i <= 10; i++)
            manager.GetOrCreateController(Guid.NewGuid(), IPAddress.Parse($"10.9.0.{i}"), ProbeSpec.Icmp, cadence, () => 60_000);
        await factory.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // every loop is inside its ping

        var sw = Stopwatch.StartNew();
        await manager.StopAsync(TimeSpan.FromSeconds(2));

        // The point is "bounded by the 2 s grace, not the 60 s probe"; 10 s proves
        // that with room for a busy CI runner.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }
}
