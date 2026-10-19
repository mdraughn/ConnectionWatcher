using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConCore;
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
        // (~2054ms measured), so this uses a generous 10 s. The engine's real TCP
        // timeout cap stays pinned in ProbeThreadingTests.
        var result = await probe.PingAsync(10_000);
        Assert.True(result.Success);
        Assert.Equal("Refused", result.Message);
    }

    [Fact]
    public async Task Silence_is_loss()
    {
        // TEST-NET-1 (RFC 5737): guaranteed unrouted — SYNs vanish.
        using var probe = new TcpConnectTransceiver(IPAddress.Parse("192.0.2.1"), 80, CancellationToken.None);
        var sw = Stopwatch.StartNew();
        var result = await probe.PingAsync(500);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}"); // the timeout bounds the connect
        Assert.False(result.Success);
        Assert.Contains(result.Message, new[] { "TimedOut", "NetworkUnreachable", "HostUnreachable" });
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

    [Fact]
    public async Task The_factory_builds_tcp_and_icmp_transceivers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var factory = new PingTransceiverFactoryIcmp();
        using var tcp = factory.Create(IPAddress.Loopback, new ProbeSpec(ProbeKind.Tcp, port), CancellationToken.None);
        var result = await tcp.PingAsync(2000);
        Assert.True(result.Success);
        Assert.Equal("Accepted", result.Message);

        using var icmp = factory.Create(IPAddress.Loopback, ProbeSpec.Icmp, CancellationToken.None);
        Assert.IsType<PingTransceiverIcmp>(icmp);
    }
}
