using System.Net;
using System.Net.Http.Json;

namespace ConWatch.Tests;

public class AdminLockTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public AdminLockTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private Task EnableGateAsync() =>
        _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });

    private async Task LogoutAsync() => await _client.PostAsync("/api/v1/auth/logout", null);

    [Fact]
    public async Task Locked_apis_return_401_and_open_apis_stay_open()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await EnableGateAsync();
        await LogoutAsync();

        // Locked: reads and writes of configuration surfaces.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.WeatherThreshold"] = "3" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.DeleteAsync("/api/v1/targets/00000000-0000-0000-0000-000000000000")).StatusCode);

        // Open: watching surfaces.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/auth/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/config/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/export/transitions?format=json")).StatusCode);
        var health = await _client.GetAsync("/api/v1/health");
        Assert.True(health.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Admin_cookie_unlocks_and_file_delete_reopens()
    {
        await EnableGateAsync(); // set-password leaves us in admin mode
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/settings")).StatusCode);

        await LogoutAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/settings")).StatusCode);

        // The documented forgot-password path: delete the file, no restart.
        var reset = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/auth/status");
        File.Delete(reset.GetProperty("resetPath").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/settings")).StatusCode);
    }

    [Fact]
    public async Task Locked_pages_redirect_to_dashboard_prompt()
    {
        await EnableGateAsync();
        await LogoutAsync();
        var noRedirect = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[] { "/Admin", "/Data", "/Config/Targets", "/Config/Profiles", "/Config/Pools", "/Traceroute", "/Setup" })
        {
            var response = await noRedirect.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/?admin=1", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Nav_hides_locked_links_and_shows_exit_when_admin()
    {
        var html = await _client.GetStringAsync("/History");
        Assert.Contains("/Config/Targets", html); // gate off: everything visible

        await EnableGateAsync(); // now in admin mode
        html = await _client.GetStringAsync("/History");
        Assert.Contains("/Config/Targets", html);
        Assert.Contains("exit admin mode", html);

        await LogoutAsync();
        html = await _client.GetStringAsync("/History");
        Assert.DoesNotContain("/Config/Targets", html);
        Assert.DoesNotContain("href=\"/Data\"", html);
        Assert.DoesNotContain("/Traceroute", html);
        Assert.DoesNotContain("/Admin", html);
    }

    [Fact]
    public async Task Dashboard_shows_lock_button_only_when_locked()
    {
        await ConfigApiTests.SeedHomeAsync(_client); // non-empty config: no wizard redirect
        var html = await _client.GetStringAsync("/");
        Assert.DoesNotContain("admin-lock-btn", html);

        await EnableGateAsync();
        await LogoutAsync();
        html = await _client.GetStringAsync("/");
        Assert.Contains("admin-lock-btn", html);
        Assert.Contains("delete this file", html); // forgot-password message with path
        Assert.Contains("admin-password.txt", html);
    }

    [Fact]
    public async Task Admin_page_shows_set_card_when_off_and_change_cards_when_admin()
    {
        var html = await _client.GetStringAsync("/Admin");
        Assert.Contains("Set admin password", html);
        Assert.Contains("not internet-grade security", html);

        await EnableGateAsync(); // admin mode
        html = await _client.GetStringAsync("/Admin");
        Assert.Contains("Change password", html);
        Assert.Contains("Remove password", html);
    }

    [Fact]
    public async Task Factory_reset_keeps_the_password()
    {
        await EnableGateAsync(); // admin mode
        var reset = await _client.PostAsJsonAsync("/api/v1/admin/factory-reset", new { confirm = "RESET" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var status = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/auth/status");
        Assert.True(status.GetProperty("enabled").GetBoolean());
    }
}
