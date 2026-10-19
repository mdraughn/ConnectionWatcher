using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ConWatch.Tests;

public class AuthApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;   // default options: cookies handled

    public AuthApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> Status() =>
        JsonSerializer.Deserialize<JsonElement>(
            await _client.GetStringAsync("/api/v1/auth/status"));

    [Fact]
    public async Task Fresh_install_is_open_and_login_is_pointless()
    {
        var status = await Status();
        Assert.False(status.GetProperty("enabled").GetBoolean());
        var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { password = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
    }

    [Fact]
    public async Task Set_password_enables_gate_and_enters_admin_mode()
    {
        var set = await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        var status = await Status();
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("admin").GetBoolean());
        Assert.False(string.IsNullOrEmpty(status.GetProperty("resetPath").GetString()));
    }

    [Fact]
    public async Task Login_logout_cycle()
    {
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        await _client.PostAsync("/api/v1/auth/logout", null);
        Assert.False((await Status()).GetProperty("admin").GetBoolean());

        var bad = await _client.PostAsJsonAsync("/api/v1/auth/login", new { password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.False((await Status()).GetProperty("admin").GetBoolean());

        var good = await _client.PostAsJsonAsync("/api/v1/auth/login", new { password = "hunter2" });
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
        Assert.True((await Status()).GetProperty("admin").GetBoolean());
    }

    [Fact]
    public async Task Login_cookie_is_the_admin_cookie_httponly_and_samesite_lax()
    {
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        await _client.PostAsync("/api/v1/auth/logout", null);

        var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { password = "hunter2" });

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("conwatch.admin=", StringComparison.Ordinal));
        var attributes = cookie.Split(';').Skip(1).Select(a => a.Trim().ToLowerInvariant()).ToList();
        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=lax", attributes);
    }

    [Fact]
    public async Task Change_requires_current_and_remove_requires_current()
    {
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "old-pass" });

        var noCurrent = await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "new-pass" });
        Assert.Equal(HttpStatusCode.Unauthorized, noCurrent.StatusCode);

        var change = await _client.PostAsJsonAsync("/api/v1/auth/set-password",
            new { current = "old-pass", password = "new-pass" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var removeBad = await _client.PostAsJsonAsync("/api/v1/auth/remove-password", new { current = "old-pass" });
        Assert.Equal(HttpStatusCode.Unauthorized, removeBad.StatusCode);
        var removeGood = await _client.PostAsJsonAsync("/api/v1/auth/remove-password", new { current = "new-pass" });
        Assert.Equal(HttpStatusCode.NoContent, removeGood.StatusCode);
        Assert.False((await Status()).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Short_password_rejected()
    {
        var set = await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "abc" });
        Assert.Equal(HttpStatusCode.BadRequest, set.StatusCode);
    }
}
