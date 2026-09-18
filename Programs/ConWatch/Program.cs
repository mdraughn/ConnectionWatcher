using ConPersistence;
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

// Registrations live in ConWatchServices, one method per concern.
builder.Services
    .AddConWatchOptions(builder.Configuration)
    .AddConWatchDatabase(builder.Configuration, out var stateDir)
    .AddConWatchEngine()
    .AddConWatchPersistence()
    .AddConWatchClone()
    .AddConWatchNotifiers()
    .AddConWatchAdminAuth(stateDir)
    .AddConWatchWeb()
    .AddConWatchHostedServices();

var app = builder.Build();

// Migrate + WAL before anything can touch the schema; hosted services start in app.Run().
await DatabaseStartup.PrepareAsync(
    app.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>(), CancellationToken.None);

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
app.MapMcp("/mcp"); // open, read-only (see the MCP design spec)
MetricsEndpoint.MapMetrics(app); // open, like the watching surface

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
locked.MapProbeTest();
locked.MapTraceroute();
locked.MapAdmin();
locked.MapSetup();
SettingsEndpoints.MapSettings(locked, open: api);

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
