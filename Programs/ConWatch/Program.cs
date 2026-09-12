using ConCore;
using ConEngine.Configuration;
using ConInfrastructure;
using ConPersistence;
using ConPersistence.Services;
using ConWatch;
using ConWatch.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

// Under the Windows Service Control Manager the working directory is
// System32 — pin the content root to the exe folder so appsettings and the
// default data/ directory resolve next to the app. Both helpers are inert
// when running normally (console, container, tests).
var isWindowsService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = isWindowsService ? AppContext.BaseDirectory : null,
});
builder.Services.AddWindowsService(options => options.ServiceName = "ConnectionWatcher");

// Options
builder.Services.Configure<PersistenceOptions>(
    builder.Configuration.GetSection(PersistenceOptions.SectionName));
builder.Services.Configure<VerdictOptions>(
    builder.Configuration.GetSection(VerdictOptions.SectionName));

// Database
var dbPath = builder.Configuration[$"{PersistenceOptions.SectionName}:DbPath"] ?? "data/conwatch.db";
var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
if (dbDir is { Length: > 0 })
    Directory.CreateDirectory(dbDir);
builder.Services.AddDbContextFactory<ConWatchDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

// Engine dependencies
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPingTransceiverFactory, PingTransceiverFactoryIcmp>();
builder.Services.AddSingleton<IDomainNameResolverFactory, DomainNameResolverFactory>();
builder.Services.AddSingleton<ConInfrastructure.Traceroute.ITtlProbe, ConInfrastructure.Traceroute.IcmpTtlProbe>();
builder.Services.AddSingleton<ConInfrastructure.Traceroute.TracerouteRunner>();
builder.Services.AddSingleton<ConInfrastructure.Discovery.IGatewayDiscovery, ConInfrastructure.Discovery.GatewayDiscovery>();
builder.Services.AddSingleton<ConInfrastructure.Discovery.IWanAddressProbe, ConInfrastructure.Discovery.UpnpWanAddressProbe>();
builder.Services.AddSingleton<SetupService>();

// Persistence services
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ConfigHealth>();
builder.Services.AddSingleton<ConPersistence.Report.IspReportBuilder>();
builder.Services.AddSingleton<TransitionRecorder>();
builder.Services.AddSingleton<GlitchRecorder>();
builder.Services.AddSingleton<RollupCollector>();

// Engine host FIRST: hosted services start sequentially, and this one owns
// the migration + seeding — nothing else may touch the schema before it.
builder.Services.AddSingleton<WatcherHostService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WatcherHostService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TransitionRecorder>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<GlitchRecorder>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RollupCollector>());
builder.Services.AddHostedService<RetentionService>();

// Cross-node clone/take-over: outbound calls to the source node.
builder.Services.AddHttpClient(ConWatch.Clone.CloneService.HttpClientName,
    c => c.Timeout = TimeSpan.FromSeconds(60)); // a full snapshot can be MBs
builder.Services.AddSingleton<ConWatch.Clone.CloneService>();
builder.Services.AddSingleton<ConWatch.Clone.CloneHealth>();

// Notifiers: idle until Mqtt.Enabled / Webhook.Enabled; reconfigured live via
// NotifierSettingsChanged, re-attached by WatcherHostService on every rebuild.
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<ConNotify.MqttNotifier>();
builder.Services.AddSingleton<ConNotify.WebhookNotifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConNotify.MqttNotifier>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConNotify.WebhookNotifier>());
builder.Services.AddHostedService<ConNotify.HeartbeatNotifier>();

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

builder.Services.AddOpenApi();
builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddSingleton<StatusBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StatusBroadcaster>());

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();

app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "ConWatch v1");
        o.RoutePrefix = "swagger";
    });
}

app.MapRazorPages();
app.MapHub<StatusHub>("/hubs/status");

var api = app.MapGroup("/api/v1");
// Watching is always open; changing requires admin mode when a password is set.
var locked = api.MapGroup("");
locked.AddEndpointFilter<ConWatch.Auth.RequireAdminFilter>();

api.MapStatus();
api.MapHistory();
api.MapGlitches();
api.MapExport();
api.MapAuth();
api.MapEngineControl(); // open route, password verified inline (take-over M2M)

locked.MapConfig();
locked.MapTraceroute();
locked.MapAdmin();
locked.MapSetup();
SettingsEndpoints.MapSettings(locked, open: api);

api.MapGet("/health", async (WatcherHostService host, TimeProvider time) =>
{
    var engine = host.Engine;
    var enabled = await host.EngineEnabledAsync();
    var body = new
    {
        // A deliberately disabled node is a healthy archive (200) — container
        // healthchecks must not flag it; 503 stays for enabled-but-down.
        Status = engine is not null ? "Healthy" : enabled ? "EngineDown" : "Disabled",
        Enabled = enabled,
        EngineRunning = engine is not null,
        TargetCount = engine?.Monitors.Count ?? 0,
        TimestampUtc = time.GetUtcNow(),
        Version = BuildInfo.Version,
        BuiltUtc = BuildInfo.BuiltUtc,
    };
    return engine is not null || !enabled ? Results.Ok(body) : Results.Json(body, statusCode: 503);
});

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    // A crash-looping service with a silent log is the worst outcome; name
    // the problem and the fix loudly instead.
    var urls = builder.Configuration["urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "(default)";
    app.Logger.LogCritical(
        "ConnectionWatcher could not start: the listen address {Urls} is already in use by another program. " +
        "Change the port (service: re-run install-service.ps1 with -Port <n>; console: --urls) and start again.",
        urls);
    throw;
}

public partial class Program { }
