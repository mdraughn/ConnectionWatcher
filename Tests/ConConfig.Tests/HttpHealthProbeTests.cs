using System.Net;
using System.Net.Sockets;
using ConConfig;

namespace ConConfig.Tests;

public sealed class HttpHealthProbeTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested = request.RequestUri;
            return respond(request);
        }
    }

    [Fact]
    public async Task Reads_the_status_word()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "status": "Healthy", "stalled": false }""")
        }));
        var r = await new HttpHealthProbe(handler).ProbeAsync("http://localhost:8080", CancellationToken.None);
        Assert.Equal("Healthy", r.Status);
        Assert.Null(r.Failure);
        Assert.Equal("http://localhost:8080/api/v1/health", handler.Requested!.ToString());
    }

    [Fact]
    public async Task Non_success_reports_the_http_code()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{ "status": "EngineDown" }""")
        }));
        var r = await new HttpHealthProbe(handler).ProbeAsync("http://localhost:8080", CancellationToken.None);
        Assert.Equal("EngineDown", r.Status); // the body still says what is wrong
        Assert.Equal("HTTP 503", r.Failure);
    }

    [Fact]
    public async Task Connection_failure_reports_refused()
    {
        var handler = new Handler(_ => throw new HttpRequestException("No connection could be made"));
        var r = await new HttpHealthProbe(handler).ProbeAsync("http://localhost:1", CancellationToken.None);
        Assert.Null(r.Status);
        Assert.Equal("refused", r.Failure);
    }

    // Real sockets from here: `localhost` resolves to ::1 and 127.0.0.1, and on
    // Windows each refused address costs ~2 s of SYN retries. Tried in turn that
    // is ~4 s, over the 3 s budget, so a dead service read as "timed out" and a
    // healthy IPv4-only one (the 0.0.0.0 default) answered only after ~2 s.

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task A_dead_localhost_port_reports_refused_not_timed_out()
    {
        var r = await new HttpHealthProbe().ProbeAsync($"http://localhost:{FreePort()}", CancellationToken.None);
        Assert.Equal("refused", r.Failure);
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
            const string body = """{ "status": "Healthy" }""";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
        });
        try
        {
            var r = await new HttpHealthProbe().ProbeAsync($"http://localhost:{port}", CancellationToken.None);
            Assert.Equal("Healthy", r.Status);
            Assert.True(r.ElapsedMs < 1000, $"took {r.ElapsedMs} ms");
        }
        finally
        {
            listener.Stop();
            await serve.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        }
    }

    [Fact]
    public async Task Timeout_reports_timed_out()
    {
        var handler = new Handler(async _ => { await Task.Delay(5000); return new HttpResponseMessage(HttpStatusCode.OK); });
        var r = await new HttpHealthProbe(handler, TimeSpan.FromMilliseconds(100)).ProbeAsync("http://localhost:8080", CancellationToken.None);
        Assert.Equal("timed out", r.Failure);
    }

    [Theory]
    [InlineData("localhost:8080")]
    [InlineData("http://")]
    [InlineData("ftp://x")]
    [InlineData("")]
    public async Task A_url_that_is_not_absolute_reports_invalid_url(string url)
    {
        var result = await new HttpHealthProbe().ProbeAsync(url, CancellationToken.None);
        Assert.Null(result.Status);
        Assert.Equal("invalid URL", result.Failure);
    }
}
