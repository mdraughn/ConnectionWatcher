using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ConConfig.Tests;

/// <summary>
/// Real sockets. `localhost` is ::1 and 127.0.0.1, the service listens on IPv4
/// only by default, and on Windows each refused address costs ~2 s of SYN
/// retries — tried one after the other, a 3 s client times out on a dead port
/// and waits 2 s on ::1 before reaching a live one.
/// </summary>
public sealed class LocalServerHttpTests
{
    private static HttpClient Client() => new(LocalServerHttp.CreateHandler()) { Timeout = TimeSpan.FromSeconds(3) };

    [Fact]
    public async Task A_dead_localhost_port_is_refused_inside_the_timeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var http = Client();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync($"http://localhost:{port}/"));
        Assert.IsType<HttpRequestException>(ex); // a timeout would be TaskCanceledException
    }

    [Fact]
    public async Task An_ipv4_only_server_answers_through_localhost_without_waiting_on_ipv6()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.ReadAsync(new byte[4096]);
            await stream.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray());
        });
        try
        {
            var detour = await Ipv6Detour.MeasureMsAsync(port);
            using var http = Client();
            var watch = Stopwatch.StartNew();
            using var response = await http.GetAsync($"http://localhost:{port}/");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            if (detour >= Ipv6Detour.DetectableMs)
                Assert.True(watch.ElapsedMilliseconds < detour, $"took {watch.ElapsedMilliseconds} ms; waiting on ::1 costs {detour} ms");
        }
        finally
        {
            listener.Stop();
            await serve.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        }
    }
}
