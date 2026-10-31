using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using ConInfrastructure;
using ConInfrastructure.Probes;

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

    [Fact]
    public async Task Loopback_echo_is_a_reply()
    {
        using var ping = new PingTransceiverIcmp(IPAddress.Loopback, CancellationToken.None);
        var result = await ping.PingAsync(timeoutMs: 5000);
        Assert.True(result.Success);
        Assert.Equal("Success", result.Message);
        Assert.True(result.RoundtripTime >= 0);
    }

    [Theory]
    [InlineData(IPStatus.Success, true)]
    [InlineData(IPStatus.DestinationHostUnreachable, false)]
    [InlineData(IPStatus.TimedOut, false)]
    [InlineData(IPStatus.TtlExpired, false)]
    public void Only_success_status_is_a_reply(IPStatus status, bool success)
    {
        var result = PingTransceiverIcmp.FromReply(IPAddress.Loopback, status, 7);
        Assert.Equal(success, result.Success);
        Assert.Equal(status.ToString(), result.Message);
        Assert.Equal(7, result.RoundtripTime);
        Assert.Equal(IPAddress.Loopback, result.IpAddress);
    }
}
