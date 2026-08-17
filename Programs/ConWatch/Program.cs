using ConCore;
using ConEngine.Configuration;
using ConInfrastructure;
using ConPersistence;
using ConPersistence.Services;
using ConWatch;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

// Persistence services
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<TransitionRecorder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TransitionRecorder>());
builder.Services.AddSingleton<RollupCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RollupCollector>());
builder.Services.AddHostedService<RetentionService>();

// Engine host
builder.Services.AddSingleton<WatcherHostService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WatcherHostService>());

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "ConWatch v1");
        o.RoutePrefix = "swagger";
    });
}

var api = app.MapGroup("/api/v1");

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

app.Run();

public partial class Program { }
