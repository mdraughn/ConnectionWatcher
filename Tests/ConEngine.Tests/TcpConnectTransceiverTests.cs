using System.Net;
using System.Net.Sockets;
using ConInfrastructure;

namespace ConEngine.Tests;

public class TcpConnectTransceiverTests
{
    [Fact]
    public async Task Accepted_connection_is_a_reply()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var probe = new TcpConnectTransceiver(IPAddress.Loopback, port, CancellationToken.None);
        var result = await probe.PingAsync(2000);
        Assert.True(result.Success);
        Assert.Equal("Accepted", result.Message);
    }

    [Fact]
    public async Task Refused_connection_is_ALSO_a_reply()
    {
        // Grab a free port then close the listener: nothing listens, the
        // stack RSTs — which proves the host is alive.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var probe = new TcpConnectTransceiver(IPAddress.Loopback, port, CancellationToken.None);
        // Windows surfaces ConnectionRefused only after ~2s of stack-level retries
        // (~2054ms measured); 2500 is the engine's actual TCP burst-mode timeout
        // cap (WatcherEngine.Activate), leaving ~450ms headroom above that latency.
        var result = await probe.PingAsync(2500);
        Assert.True(result.Success);
        Assert.Equal("Refused", result.Message);
    }

    [Fact]
    public async Task Silence_is_loss()
    {
        // TEST-NET-1 (RFC 5737): guaranteed unrouted — SYNs vanish.
        using var probe = new TcpConnectTransceiver(IPAddress.Parse("192.0.2.1"), 80, CancellationToken.None);
        var result = await probe.PingAsync(500);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Engine_cancellation_throws_instead_of_reporting_loss()
    {
        using var cts = new CancellationTokenSource();
        using var probe = new TcpConnectTransceiver(IPAddress.Parse("192.0.2.1"), 80, cts.Token);
        var task = probe.PingAsync(5000);
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
