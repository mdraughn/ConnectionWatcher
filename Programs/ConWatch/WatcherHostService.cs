using ConCore;
using ConEngine;
using ConEngine.Configuration;
using ConPersistence;
using ConPersistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConWatch;

/// <summary>
/// Owns the engine lifecycle: migrate + seed at startup, build the engine from
/// DB config, attach the recorder and collector, and rebuild (debounced) when
/// the configuration changes.
/// </summary>
public class WatcherHostService(
    IDbContextFactory<ConWatchDbContext> dbFactory,
    ConfigStore store,
    ConfigService configService,
    IPingTransceiverFactory pingFactory,
    IDomainNameResolverFactory resolverFactory,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    IOptions<VerdictOptions> verdictOptions,
    IOptions<PersistenceOptions> persistenceOptions,
    IConfiguration configuration,
    TransitionRecorder recorder,
    RollupCollector collector) : IHostedService
{
    private readonly ILogger<WatcherHostService> _log = loggerFactory.CreateLogger<WatcherHostService>();
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private CancellationToken _appStopping;

    /// <summary>The running engine, or null before start / mid-rebuild.</summary>
    public WatcherEngine? Engine { get; private set; }

    public bool EngineRunning => Engine is not null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _appStopping = cancellationToken;

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        if (await store.IsEmptyAsync())
        {
            var seed = configuration.GetSection(WatcherOptions.SectionName).Exists()
                ? configuration.GetSection(WatcherOptions.SectionName).Get<WatcherOptions>()
                : null;
            await store.SeedAsync(seed);
        }

        await StartEngineAsync();
        configService.ConfigChanged += OnConfigChanged;
        _log.LogInformation("WatcherHostService started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        configService.ConfigChanged -= OnConfigChanged;
        _debounceCts?.Cancel();
        // Serialize with any in-flight rebuild: two concurrent engine.StopAsync
        // calls race inside the coordinators' Dispose.
        await _rebuildLock.WaitAsync(CancellationToken.None);
        try
        {
            if (Engine is { } engine)
            {
                Engine = null;
                await engine.StopAsync();
            }
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private async Task StartEngineAsync()
    {
        var options = await store.LoadWatcherOptionsAsync();
        var validation = new WatcherOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            // Should be unreachable: every write was pre-validated.
            _log.LogError("Stored configuration is invalid; engine not started: {Failures}",
                validation.FailureMessage);
            return;
        }

        var engine = new WatcherEngine(timeProvider, pingFactory, resolverFactory,
            loggerFactory, Options.Create(options), verdictOptions);
        recorder.Attach(engine.TransitionSource);
        collector.Attach(engine.PingSampleSource);
        await engine.StartAsync(_appStopping);
        Engine = engine;
        _log.LogInformation("Engine started with {Count} targets", options.Targets.Count);
    }

    /// <summary>Stops the engine and rebuilds it from current DB config.</summary>
    public async Task RebuildAsync()
    {
        await _rebuildLock.WaitAsync();
        try
        {
            _log.LogInformation("Rebuilding engine after config change");
            if (Engine is { } engine)
            {
                Engine = null;
                await engine.StopAsync();
            }
            await StartEngineAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Engine rebuild failed");
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private void OnConfigChanged()
    {
        _debounceCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_appStopping);
        _debounceCts = cts;
        var delay = TimeSpan.FromSeconds(persistenceOptions.Value.RebuildDebounceSeconds);
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, timeProvider, cts.Token);
                await RebuildAsync();
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer change or shutdown.
            }
        }, CancellationToken.None);
    }
}
