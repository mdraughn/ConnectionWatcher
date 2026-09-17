using System.Diagnostics;
using System.Net;
using ConInfrastructure;

namespace ConEngine.Tests;

public class PingTransceiverIcmpTests
{
    [Fact]
    public async Task Cancellation_ends_an_in_flight_ping_before_its_timeout()
    {
        // 192.0.2.1 is TEST-NET-1: never routed, never answers.
        using var cts = new CancellationTokenSource();
        using var ping = new PingTransceiverIcmp(IPAddress.Parse("192.0.2.1"), cts.Token);
        var sw = Stopwatch.StartNew();
        var task = ping.PingAsync(timeoutMs: 10_000);
        cts.CancelAfter(200);
        var result = await task;

        Assert.False(result.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }
}
