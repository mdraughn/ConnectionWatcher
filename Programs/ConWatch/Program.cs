using ConCore;
using ConEngine.Configuration;
using ConInfrastructure;
using ConPersistence;
using ConPersistence.Services;
using ConWatch;
using ConWatch.Api;
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
builder.Services.AddSingleton<RollupCollector>();

// Engine host FIRST: hosted services start sequentially, and this one owns
// the migration + seeding — nothing else may touch the schema before it.
builder.Services.AddSingleton<WatcherHostService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WatcherHostService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TransitionRecorder>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RollupCollector>());
builder.Services.AddHostedService<RetentionService>();

builder.Services.AddOpenApi();
builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddSingleton<StatusBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StatusBroadcaster>());

var app = builder.Build();

app.UseStaticFiles();

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
api.MapConfig();
api.MapStatus();
api.MapHistory();
api.MapTraceroute();
api.MapSettings();
api.MapAdmin();
api.MapSetup();
api.MapExport();

api.MapGet("/health", (WatcherHostService host, TimeProvider time) =>
{
    var engine = host.Engine;
    var body = new
    {
        Status = engine is not null ? "Healthy" : "EngineDown",
        EngineRunning = engine is not null,
        TargetCount = engine?.Monitors.Count ?? 0,
        TimestampUtc = time.GetUtcNow(),
    };
    return engine is not null ? Results.Ok(body) : Results.Json(body, statusCode: 503);
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
