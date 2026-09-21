using System.Net;
using ConWatchCli.Services;

namespace ConWatchCli.Tests;

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

    [Fact]
    public async Task Timeout_reports_timed_out()
    {
        var handler = new Handler(async _ => { await Task.Delay(5000); return new HttpResponseMessage(HttpStatusCode.OK); });
        var r = await new HttpHealthProbe(handler, TimeSpan.FromMilliseconds(100)).ProbeAsync("http://localhost:8080", CancellationToken.None);
        Assert.Equal("timed out", r.Failure);
    }
}
