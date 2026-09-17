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

    [Fact]
    public async Task StopAsync_with_ten_hanging_controllers_finishes_within_the_grace_period()
    {
        using var cts = new CancellationTokenSource();
        var manager = new PingManager(TimeProvider.System, new HangingFactory(), cts.Token, NullLoggerFactory.Instance);
        var cadence = new FixedIntervalCadence(TimeSpan.FromMilliseconds(1), TimeProvider.System);
        for (int i = 1; i <= 10; i++)
            manager.GetOrCreateController(Guid.NewGuid(), IPAddress.Parse($"10.9.0.{i}"), ProbeSpec.Icmp, cadence, () => 60_000);
        await Task.Delay(50); // let every loop enter its ping

        var sw = Stopwatch.StartNew();
        await manager.StopAsync(TimeSpan.FromSeconds(2));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }
}
