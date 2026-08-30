# Admin Password Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A single site password gating all configuration changes (Config/Admin/Traceroute/Setup pages and their APIs), stored as a hash in a hand-deletable file beside the database.

**Architecture:** An `AdminGate` singleton owns the password file (PBKDF2 hash). Cookie authentication marks a browser as "admin" for the session. An endpoint filter guards the protected API group; protected Razor pages redirect to the dashboard's password prompt; the nav hides locked links. No file → gate off → today's behavior.

**Tech Stack:** ASP.NET Core cookie auth (no Identity), `Rfc2898DeriveBytes.Pbkdf2`, Data Protection keys persisted to the data dir, xUnit + `ConWatchFactory` integration tests.

**Spec:** `docs/superpowers/specs/2026-08-30-admin-password-design.md`

## Global Constraints

- Branch: `admin-password` off master. TDD per task; commit per task.
- Warning copy (verbatim, used on set AND change cards): *"This prevents casual tinkering with your configuration. It is not internet-grade security — the password is sent over plain HTTP on your network, and anyone with access to the server's files can remove it."*
- Password file name: `admin-password.txt`, in the directory of `Persistence:DbPath`. Format: `pbkdf2$100000$<saltBase64>$<hashBase64>` (SHA-256, 16-byte salt, 32-byte hash).
- Open surfaces (never gated): `/`, `/History`, `/Report`, `GET /api/v1/status`, history APIs, `GET /api/v1/config/health`, `/api/v1/export/*`, `/api/v1/health`, `/hubs/status`, all `/api/v1/auth/*`.
- Locked surfaces (gate on, no admin cookie): pages `/Config/*`, `/Admin`, `/Traceroute`, `/Setup` (redirect to `/?admin=1`); APIs config CRUD + config export/import, `GET`+`PUT /api/v1/settings`, `/api/v1/admin/*`, setup, traceroute (401 `{"errors":["Admin password required."]}`).
- Failed login: fixed 1000 ms delay, then 401. Min password length 4.
- Factory reset must NOT delete the password file.

---

### Task 1: AdminGate service

**Files:**
- Create: `Programs/ConWatch/Auth/AdminGate.cs`
- Test: `Tests/ConWatch.Tests/AdminGateTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `class AdminGate(string filePath, ILogger<AdminGate> log)` with `bool IsEnabled`, `bool Verify(string password)`, `void Set(string password)`, `void Remove()`, `string FilePath`, and `bool IsLocked(System.Security.Claims.ClaimsPrincipal user)` (true iff `IsEnabled` and user not authenticated). Tasks 2–5 depend on these exact names.

- [ ] **Step 1: Write the failing tests**

```csharp
using ConWatch.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConWatch.Tests;

public class AdminGateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("conwatch-gate").FullName;
    private readonly AdminGate _gate;

    public AdminGateTests() =>
        _gate = new AdminGate(Path.Combine(_dir, "admin-password.txt"), NullLogger<AdminGate>.Instance);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Disabled_until_set_then_verifies_round_trip()
    {
        Assert.False(_gate.IsEnabled);
        _gate.Set("hunter2");
        Assert.True(_gate.IsEnabled);
        Assert.True(_gate.Verify("hunter2"));
        Assert.False(_gate.Verify("wrong"));
        Assert.False(_gate.Verify(""));
    }

    [Fact]
    public void File_is_a_hash_not_the_password()
    {
        _gate.Set("hunter2");
        var content = File.ReadAllText(_gate.FilePath);
        Assert.StartsWith("pbkdf2$100000$", content);
        Assert.DoesNotContain("hunter2", content);
    }

    [Fact]
    public void Remove_and_manual_delete_both_disable()
    {
        _gate.Set("hunter2");
        _gate.Remove();
        Assert.False(_gate.IsEnabled);
        _gate.Set("again");
        File.Delete(_gate.FilePath); // the documented forgot-password path
        Assert.False(_gate.IsEnabled);
    }

    [Fact]
    public void Corrupt_file_is_enabled_but_never_verifies()
    {
        File.WriteAllText(_gate.FilePath, "garbage");
        Assert.True(_gate.IsEnabled);   // never fail open
        Assert.False(_gate.Verify("anything"));
    }

    [Fact]
    public void IsLocked_depends_on_enabled_and_authentication()
    {
        var anon = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        var admin = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity("Cookies"));
        Assert.False(_gate.IsLocked(anon));      // gate off -> open
        _gate.Set("hunter2");
        Assert.True(_gate.IsLocked(anon));
        Assert.False(_gate.IsLocked(admin));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~AdminGateTests"`
Expected: compile error — `AdminGate` does not exist.

- [ ] **Step 3: Implement AdminGate**

```csharp
using System.Security.Claims;
using System.Security.Cryptography;

namespace ConWatch.Auth;

/// <summary>
/// The single site password, stored as a salted PBKDF2 hash in a
/// hand-deletable file beside the database. No file means no protection;
/// deleting the file is the documented forgot-password path and takes
/// effect on the next request. A corrupt file counts as enabled but never
/// verifies — the gate must not fail open.
/// </summary>
public class AdminGate(string filePath, ILogger<AdminGate> log)
{
    private const int Iterations = 100_000;

    public string FilePath { get; } = filePath;

    public bool IsEnabled => File.Exists(FilePath);

    public bool IsLocked(ClaimsPrincipal user) =>
        IsEnabled && user.Identity?.IsAuthenticated != true;

    public void Set(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        File.WriteAllText(FilePath,
            $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public void Remove()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    public bool Verify(string password)
    {
        try
        {
            var parts = File.ReadAllText(FilePath).Trim().Split('$');
            if (parts is not ["pbkdf2", _, _, _] || !int.TryParse(parts[1], out var iterations))
            {
                log.LogWarning("Admin password file {Path} is corrupt; delete it to reset", FilePath);
                return false;
            }
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations,
                HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            log.LogWarning(ex, "Admin password file {Path} unreadable; delete it to reset", FilePath);
            return false;
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~AdminGateTests"`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/Auth/AdminGate.cs Tests/ConWatch.Tests/AdminGateTests.cs
git commit -m "feat(auth): AdminGate password-file service"
```

---

### Task 2: Cookie auth + auth endpoints

**Files:**
- Create: `Programs/ConWatch/Api/AuthEndpoints.cs`
- Modify: `Programs/ConWatch/Program.cs` (services + `UseAuthentication` + map)
- Test: `Tests/ConWatch.Tests/AuthApiTests.cs`

**Interfaces:**
- Consumes: `AdminGate` from Task 1.
- Produces: DI-registered `AdminGate`; endpoints `POST /api/v1/auth/login {password}`, `POST /api/v1/auth/logout`, `GET /api/v1/auth/status` → `{enabled, admin, resetPath}`, `POST /api/v1/auth/set-password {current?, password}`, `POST /api/v1/auth/remove-password {current}`. Login/set-password sign in the cookie. Task 3's filter reads `context.HttpContext.User`.

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~AuthApiTests"`
Expected: FAIL — 404s (endpoints missing).

- [ ] **Step 3: Implement AuthEndpoints**

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ConWatch.Auth;

namespace ConWatch.Api;

/// <summary>Login/logout and password management for the admin gate.</summary>
public static class AuthEndpoints
{
    public record LoginRequest(string? Password);
    public record SetPasswordRequest(string? Current, string? Password);
    public record RemovePasswordRequest(string? Current);

    private static Task SignInAdminAsync(HttpContext http) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "admin")],
                CookieAuthenticationDefaults.AuthenticationScheme)));

    public static void MapAuth(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapGet("/status", (AdminGate gate, HttpContext http) => Results.Ok(new
        {
            Enabled = gate.IsEnabled,
            Admin = http.User.Identity?.IsAuthenticated == true,
            ResetPath = gate.FilePath,
        }));

        auth.MapPost("/login", async (LoginRequest request, AdminGate gate, HttpContext http) =>
        {
            if (!gate.IsEnabled)
                return Results.BadRequest(new { errors = new[] { "No admin password is set." } });
            if (!gate.Verify(request.Password ?? ""))
            {
                await Task.Delay(1000); // fixed brute-force friction
                return Results.Unauthorized();
            }
            await SignInAdminAsync(http);
            return Results.NoContent();
        });

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        auth.MapPost("/set-password", async (SetPasswordRequest request, AdminGate gate, HttpContext http) =>
        {
            if (request.Password is not { Length: >= 4 })
                return Results.BadRequest(new { errors = new[] { "Password must be at least 4 characters." } });
            if (gate.IsEnabled && !gate.Verify(request.Current ?? ""))
            {
                await Task.Delay(1000);
                return Results.Unauthorized();
            }
            gate.Set(request.Password);
            await SignInAdminAsync(http); // never lock the admin out of the page they're on
            return Results.NoContent();
        });

        auth.MapPost("/remove-password", async (RemovePasswordRequest request, AdminGate gate) =>
        {
            if (!gate.IsEnabled)
                return Results.BadRequest(new { errors = new[] { "No admin password is set." } });
            if (!gate.Verify(request.Current ?? ""))
            {
                await Task.Delay(1000);
                return Results.Unauthorized();
            }
            gate.Remove();
            return Results.NoContent();
        });
    }
}
```

- [ ] **Step 4: Wire Program.cs**

In `Program.cs`, after the notifier registrations (before `AddOpenApi`):

```csharp
// Admin gate: password file lives beside the database; cookies mark a
// browser as admin for the session. Keys persisted so cookies survive a
// service restart.
builder.Services.AddSingleton(sp => new ConWatch.Auth.AdminGate(
    Path.Combine(dbDir is { Length: > 0 } ? dbDir : ".", "admin-password.txt"),
    sp.GetRequiredService<ILogger<ConWatch.Auth.AdminGate>>()));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(dbDir is { Length: > 0 } ? dbDir : ".", "keys")));
builder.Services.AddAuthentication(
        Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "conwatch.admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // APIs get plain status codes, never login-page redirects.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
```

After `app.UseStaticFiles();` add `app.UseAuthentication();`, and with the other group mappings add `api.MapAuth();`.

Requires `using Microsoft.AspNetCore.DataProtection;` at the top of Program.cs.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~AuthApiTests"`
Expected: 5 passed. Also run the full ConWatch suite — nothing else may regress.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(auth): cookie auth and /api/v1/auth endpoints"
```

---

### Task 3: API enforcement

**Files:**
- Create: `Programs/ConWatch/Auth/RequireAdminFilter.cs`
- Modify: `Programs/ConWatch/Program.cs` (split open/locked groups)
- Modify: `Programs/ConWatch/Api/SettingsEndpoints.cs` (config/health moves to the open group)
- Test: `Tests/ConWatch.Tests/AdminLockTests.cs`

**Interfaces:**
- Consumes: `AdminGate.IsLocked(ClaimsPrincipal)` (Task 1), auth endpoints (Task 2).
- Produces: locked API group; `SettingsEndpoints.MapSettings(RouteGroupBuilder locked, RouteGroupBuilder open)` new signature (config/health mapped on `open`).

- [ ] **Step 1: Write the failing tests**

```csharp
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
    public async Task Factory_reset_keeps_the_password()
    {
        await EnableGateAsync(); // admin mode
        var reset = await _client.PostAsJsonAsync("/api/v1/admin/factory-reset", new { confirm = "RESET" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var status = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/auth/status");
        Assert.True(status.GetProperty("enabled").GetBoolean());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~AdminLockTests"`
Expected: FAIL — locked endpoints return 200/404 instead of 401.

- [ ] **Step 3: Implement the filter**

```csharp
namespace ConWatch.Auth;

/// <summary>401s any request to a protected endpoint when the gate is on
/// and the caller lacks the admin cookie. Inert while no password is set.</summary>
public class RequireAdminFilter(AdminGate gate) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (gate.IsLocked(context.HttpContext.User))
            return Results.Json(new { errors = new[] { "Admin password required." } }, statusCode: 401);
        return await next(context);
    }
}
```

- [ ] **Step 4: Split the groups in Program.cs**

Replace the endpoint-group block with:

```csharp
var api = app.MapGroup("/api/v1");
// Watching is always open; changing requires admin mode when a password is set.
var locked = api.MapGroup("");
locked.AddEndpointFilter<ConWatch.Auth.RequireAdminFilter>();

api.MapStatus();
api.MapHistory();
api.MapExport();
api.MapAuth();

locked.MapConfig();
locked.MapTraceroute();
locked.MapAdmin();
locked.MapSetup();
SettingsEndpoints.MapSettings(locked, open: api);
```

And change `SettingsEndpoints.MapSettings` to:

```csharp
public static void MapSettings(RouteGroupBuilder locked, RouteGroupBuilder open)
{
    open.MapGet("/config/health", async (ConfigHealth health) =>
        new { warnings = await health.EvaluateAsync() });

    locked.MapGet("/settings", async (SettingsStore store) => await store.GetAllAsync());

    locked.MapPut("/settings", /* existing handler body unchanged */);
}
```

(Keep the existing PUT handler body verbatim; only the receiver group changes.)

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test Tests/ConWatch.Tests`
Expected: all pass, including existing suites (they never set a password, so the filter is inert for them).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(auth): RequireAdmin filter guards config/admin/setup/traceroute APIs"
```

---

### Task 4: Page gating + nav

**Files:**
- Modify: `Programs/ConWatch/Pages/Admin.cshtml.cs`, `Config/Targets.cshtml.cs`, `Config/Profiles.cshtml.cs`, `Config/Pools.cshtml.cs`, `Traceroute.cshtml.cs`, `Setup.cshtml.cs`
- Modify: `Programs/ConWatch/Pages/_Layout.cshtml`
- Test: `Tests/ConWatch.Tests/AdminLockTests.cs` (extend)

**Interfaces:**
- Consumes: `AdminGate.IsLocked(User)` (Task 1); cookie auth (Task 2).
- Produces: locked pages 302 → `/?admin=1`; nav links conditional.

- [ ] **Step 1: Write the failing tests** (append to `AdminLockTests`)

```csharp
[Fact]
public async Task Locked_pages_redirect_to_dashboard_prompt()
{
    await EnableGateAsync();
    await LogoutAsync();
    var noRedirect = _factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    foreach (var url in new[] { "/Admin", "/Config/Targets", "/Config/Profiles", "/Config/Pools", "/Traceroute", "/Setup" })
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
    Assert.DoesNotContain("/Traceroute", html);
    Assert.DoesNotContain("/Admin", html);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~AdminLockTests"`
Expected: new tests FAIL (pages render 200; nav always shows links).

- [ ] **Step 3: Gate the pages**

Each protected page model gets `AdminGate gate` added to its primary constructor and this first line in `OnGet`/`OnGetAsync` (converting `void OnGet()` to `IActionResult OnGet()` and `async Task OnGetAsync()` to `async Task<IActionResult> OnGetAsync()`, adding `return Page();` at the end):

```csharp
if (gate.IsLocked(User))
    return Redirect("/?admin=1");
```

`Setup.cshtml.cs` and `Traceroute.cshtml.cs` become `IActionResult OnGet()`; the three Config models and Admin become `async Task<IActionResult> OnGetAsync()`.

- [ ] **Step 4: Conditional nav in `_Layout.cshtml`**

```cshtml
@inject ConWatch.Auth.AdminGate Gate
...
<ul>
    <li><a href="/">Dashboard</a></li>
    <li><a href="/History">History</a></li>
    @if (!Gate.IsLocked(User))
    {
        <li><a href="/Config/Targets">Config</a></li>
        <li><a href="/Traceroute">Traceroute</a></li>
        <li><a href="/Admin">Admin</a></li>
        @if (Gate.IsEnabled)
        {
            <li><a href="#" id="nav-exit-admin"
                   onclick="fetch('/api/v1/auth/logout',{method:'POST'}).then(()=>location.href='/');return false;">
                   exit admin mode</a></li>
        }
    }
</ul>
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test Tests/ConWatch.Tests`
Expected: all pass (PageSmokeTests still pass — no password set there).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(auth): hide and redirect protected pages when locked"
```

---

### Task 5: Dashboard prompt + Admin password cards

**Files:**
- Modify: `Programs/ConWatch/Pages/Index.cshtml` + `Index.cshtml.cs` (lock button + dialog)
- Modify: `Programs/ConWatch/Pages/Admin.cshtml` (+ page model already gated) — password cards
- Modify: `Programs/ConWatch/wwwroot/lib/site-admin.js` (card wiring)
- Test: `Tests/ConWatch.Tests/AdminLockTests.cs` (extend), `PageSmokeTests`

**Interfaces:**
- Consumes: auth endpoints (Task 2), `AdminGate` (Task 1).
- Produces: UI only; no new APIs.

- [ ] **Step 1: Write the failing tests** (append to `AdminLockTests`)

```csharp
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
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~AdminLockTests"`
Expected: FAIL.

- [ ] **Step 3: Dashboard lock button + dialog**

`Index.cshtml.cs`: inject `AdminGate gate`, expose `public bool ShowLock => gate.IsLocked(User);` and `public string ResetPath => gate.FilePath;` (keep the existing wizard-redirect logic).

`Index.cshtml`, after the main content:

```cshtml
@if (Model.ShowLock)
{
    <p style="text-align:right">
        <button id="admin-lock-btn" class="outline secondary" onclick="document.getElementById('admin-dialog').showModal()">🔒 Admin</button>
    </p>
    <dialog id="admin-dialog">
        <article>
            <h4>Enter admin password</h4>
            <ul class="form-errors" id="admin-dialog-errors"></ul>
            <form id="admin-login-form">
                <input type="password" id="admin-password" autocomplete="current-password" autofocus />
                <button type="submit">Enable admin mode</button>
                <button type="button" class="secondary outline" onclick="document.getElementById('admin-dialog').close()">Cancel</button>
            </form>
            <details>
                <summary>Forgot the password?</summary>
                <p>On the machine running ConnectionWatcher, delete this file and reload:
                   <code>@Model.ResetPath</code></p>
            </details>
        </article>
    </dialog>
    <script>
        document.getElementById("admin-login-form").addEventListener("submit", async (e) => {
            e.preventDefault();
            const r = await fetch("/api/v1/auth/login", {
                method: "POST", headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ password: document.getElementById("admin-password").value }),
            });
            if (r.ok) location.reload();
            else document.getElementById("admin-dialog-errors").innerHTML = "<li>Wrong password.</li>";
        });
        if (new URLSearchParams(location.search).get("admin") === "1")
            document.getElementById("admin-dialog").showModal();
    </script>
}
```

- [ ] **Step 4: Admin page cards**

`Admin.cshtml.cs`: inject `AdminGate gate`; expose `public bool PasswordSet => gate.IsEnabled;`.

In `Admin.cshtml`, before the Danger zone:

```cshtml
<h3>Admin password</h3>
<ul class="form-errors" id="password-errors"></ul>
<p><small>This prevents casual tinkering with your configuration. It is not
internet-grade security &mdash; the password is sent over plain HTTP on your
network, and anyone with access to the server's files can remove it.</small></p>
@if (!Model.PasswordSet)
{
    <form id="password-form" data-mode="set">
        <div class="grid">
            <label>New password <input type="password" name="password" autocomplete="new-password" /></label>
            <label>Confirm <input type="password" name="confirm" autocomplete="new-password" /></label>
        </div>
        <button type="submit">Set admin password</button>
    </form>
}
else
{
    <form id="password-form" data-mode="change">
        <div class="grid">
            <label>Current password <input type="password" name="current" autocomplete="current-password" /></label>
            <label>New password <input type="password" name="password" autocomplete="new-password" /></label>
            <label>Confirm <input type="password" name="confirm" autocomplete="new-password" /></label>
        </div>
        <button type="submit">Change password</button>
        <button type="button" id="password-remove-btn" class="secondary">Remove password</button>
    </form>
}
```

In `site-admin.js` (guarded — the form only exists on the Admin page):

```javascript
const passwordForm = document.getElementById("password-form");
if (passwordForm) {
    passwordForm.addEventListener("submit", async (e) => {
        e.preventDefault();
        const errors = document.getElementById("password-errors");
        clearErrors(errors);
        const f = new FormData(passwordForm);
        if (f.get("password") !== f.get("confirm")) {
            showErrors(errors, { body: { errors: ["Passwords do not match."] } });
            return;
        }
        const result = await apiFetch("POST", "/api/v1/auth/set-password",
            { current: f.get("current") || null, password: f.get("password") });
        if (result.ok) location.reload();
        else showErrors(errors, result.status === 401
            ? { body: { errors: ["Current password is wrong."] } } : result);
    });
    document.getElementById("password-remove-btn")?.addEventListener("click", async () => {
        const errors = document.getElementById("password-errors");
        clearErrors(errors);
        const current = new FormData(passwordForm).get("current");
        const result = await apiFetch("POST", "/api/v1/auth/remove-password", { current });
        if (result.ok) location.reload();
        else showErrors(errors, { body: { errors: ["Enter the current password above, then click Remove."] } });
    });
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test Tests/ConWatch.Tests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(auth): dashboard password prompt and Admin password cards"
```

---

### Task 6: Wrap-up

**Files:**
- Modify: `README.md` (short "Admin password" paragraph in the Web UI section)
- Modify: `docs/superpowers/BACKLOG.md` if anything was deferred

- [ ] **Step 1: README** — add under the Web UI section:

```markdown
**Admin password** (optional) — set one on the Admin page to lock Config,
Admin, Traceroute, and Setup (pages and APIs) behind a single site password.
Watching stays open. A 🔒 button on the dashboard enters admin mode for the
browser session. It stops casual tinkering, not attackers: forgot the
password? Delete `admin-password.txt` next to the database
(`C:\ProgramData\DSoftCo\ConnectionWatcher\data\` on Windows, the mapped
`data/` folder on Docker/Synology) and reload.
```

- [ ] **Step 2: Full verification**

Run: `dotnet test` (all four suites) and `npm test` in `Programs/ConWatch/ClientApp`.
Expected: everything green.

- [ ] **Step 3: Commit, then use superpowers:finishing-a-development-branch**

```bash
git add -A
git commit -m "docs: admin password README section"
```
