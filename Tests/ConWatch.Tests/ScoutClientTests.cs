using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Discovery;
using ConInfrastructure.Discovery;

namespace ConWatch.Tests;

public class ScoutClientTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly ScoutItem[] OneDevice =
    [
        new("192.168.1.20", "nas.example.com", DiscoveryKind.LocalDevice, DiscoveryMethod.ArpTable,
            "tray:TESTPC", "MAC aa:bb:cc:00:11:33"),
    ];

    private HttpClient Client() => _factory.CreateClient();          // cookie-enabled, in-memory server

    private static Uri Local => new("http://localhost/");

    private async Task<JsonElement> ListAsync(HttpClient admin) =>
        JsonSerializer.Deserialize<JsonElement>(await admin.GetStringAsync("/api/v1/discoveries?status=all"));

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw ex;
    }

    [Fact]
    public async Task An_open_server_accepts_the_post_without_any_password()
    {
        var client = Client();

        var result = await new ScoutClient(client, Local).SendAsync(OneDevice, null, false, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal((1, 0), (result.Created, result.Updated));
        var row = Assert.Single((await ListAsync(client)).EnumerateArray());
        Assert.Equal("tray:TESTPC", row.GetProperty("origin").GetString());
        Assert.Equal("nas.example.com", row.GetProperty("hostname").GetString());
    }

    [Fact]
    public async Task A_locked_server_without_a_password_says_a_password_is_required()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();

        var result = await new ScoutClient(Client(), Local).SendAsync(OneDevice, null, false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScoutFailure.PasswordRequired, result.Failure);
        Assert.Empty((await ListAsync(admin)).EnumerateArray());
    }

    [Fact]
    public async Task A_wrong_password_is_reported_and_nothing_is_stored()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();

        var result = await new ScoutClient(Client(), Local).SendAsync(OneDevice, "wrong", false, CancellationToken.None);

        Assert.Equal(ScoutFailure.WrongPassword, result.Failure);
        Assert.DoesNotContain("wrong", result.Message);
        Assert.Empty((await ListAsync(admin)).EnumerateArray());
    }

    [Fact]
    public async Task The_right_password_logs_in_posts_and_keeps_the_session_for_the_next_send()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var scout = new ScoutClient(Client(), Local);

        var first = await scout.SendAsync(OneDevice, "hunter2", false, CancellationToken.None);
        var second = await scout.SendAsync(OneDevice, null, false, CancellationToken.None);   // cookie, no password

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.Equal((0, 1), (second.Created, second.Updated));
    }

    [Fact]
    public async Task The_password_is_never_sent_over_plain_http_to_another_host_without_an_explicit_yes()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var remote = new Uri("http://scout-test.example.com/");   // plain http, not loopback

        var blocked = await new ScoutClient(Client(), remote).SendAsync(OneDevice, "hunter2", false, CancellationToken.None);
        var allowed = await new ScoutClient(Client(), remote).SendAsync(OneDevice, "hunter2", true, CancellationToken.None);

        Assert.Equal(ScoutFailure.InsecureTransport, blocked.Failure);
        Assert.True(allowed.Success, allowed.Message);
    }

    [Fact]
    public async Task An_https_server_needs_no_override()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();

        var result = await new ScoutClient(Client(), new Uri("https://scout-test.example.com/"))
            .SendAsync(OneDevice, "hunter2", false, CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task A_batch_the_server_rejects_comes_back_as_Rejected_with_the_servers_words()
    {
        var bad = new[] { new ScoutItem("router.example.com", null, DiscoveryKind.Gateway, DiscoveryMethod.DefaultRoute, "tray:X", null) };

        var result = await new ScoutClient(Client(), Local).SendAsync(bad, null, false, CancellationToken.None);

        Assert.Equal(ScoutFailure.Rejected, result.Failure);
        Assert.Contains("not a usable IP address", result.Message);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]     // an HttpClient timeout looks like this
    public async Task An_unreachable_or_timing_out_server_is_Unreachable_and_names_the_address(Type exceptionType)
    {
        var http = new HttpClient(new ThrowingHandler((Exception)Activator.CreateInstance(exceptionType, "nope")!));

        var result = await new ScoutClient(http, new Uri("http://nas.example.com:8080/"))
            .SendAsync(OneDevice, null, false, CancellationToken.None);

        Assert.Equal(ScoutFailure.Unreachable, result.Failure);
        Assert.Contains("nas.example.com", result.Message);
    }

    [Fact]
    public async Task A_cancelled_send_is_cancellation_not_Unreachable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ScoutClient(Client(), Local).SendAsync(OneDevice, null, false, cts.Token));
    }

    private sealed class RecordingStub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task A_redirect_on_the_login_post_fails_and_the_password_never_reaches_the_target()
    {
        var stub = new RecordingStub(r => r.RequestUri!.AbsolutePath.EndsWith("/discoveries")
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
              { Headers = { Location = new Uri("http://evil.example.com/api/v1/auth/login") } });

        var result = await new ScoutClient(new HttpClient(stub), new Uri("https://scout-test.example.com/"))
            .SendAsync(OneDevice, "hunter2", false, CancellationToken.None);

        Assert.Equal(ScoutFailure.ServerError, result.Failure);
        Assert.DoesNotContain("hunter2", result.Message);
        Assert.DoesNotContain(stub.Requests, u => u.Host == "evil.example.com");
    }

    [Fact]
    public async Task A_redirect_on_the_items_post_fails()
    {
        var stub = new RecordingStub(_ => new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
            { Headers = { Location = new Uri("http://evil.example.com/") } });

        var result = await new ScoutClient(new HttpClient(stub), new Uri("http://scout-test.example.com/"))
            .SendAsync(OneDevice, null, false, CancellationToken.None);

        Assert.Equal(ScoutFailure.ServerError, result.Failure);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task A_blocked_insecure_sign_in_sends_no_login_request_at_all()
    {
        var stub = new RecordingStub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await new ScoutClient(new HttpClient(stub), new Uri("http://scout-test.example.com/"))
            .SendAsync(OneDevice, "hunter2", false, CancellationToken.None);

        Assert.Equal(ScoutFailure.InsecureTransport, result.Failure);
        Assert.DoesNotContain(stub.Requests, u => u.AbsolutePath.Contains("/auth/login"));
    }

    [Fact]
    public void CreateHandler_never_follows_redirects_and_keeps_its_own_cookies()
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(ScoutClient.CreateHandler());

        Assert.False(handler.AllowAutoRedirect);
        Assert.True(handler.UseCookies);
        Assert.NotNull(handler.CookieContainer);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, handler.PooledConnectionLifetime);
    }
}