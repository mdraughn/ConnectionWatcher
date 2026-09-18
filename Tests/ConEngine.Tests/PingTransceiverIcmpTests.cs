using System.Diagnostics;
using System.Net;
using ConInfrastructure;

namespace ConEngine.Tests;

public class PingTransceiverIcmpTests
{
    [Fact]
    public async Task Cancellation_ends_an_in_flight_ping_before_its_timeout_and_throws()
    {
        // 192.0.2.1 is TEST-NET-1: never routed, never answers.
        using var cts = new CancellationTokenSource();
        using var ping = new PingTransceiverIcmp(IPAddress.Parse("192.0.2.1"), cts.Token);
        var sw = Stopwatch.StartNew();
        var task = ping.PingAsync(timeoutMs: 10_000);
        cts.CancelAfter(200);

        // The engine's own cancellation is not a lost packet: it must propagate,
        // not come back as a failed sample that the tracker would record.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }
}
