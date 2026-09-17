using ConCore;
using ConEngine;
using ConEngine.Configuration;
using ConEngine.Tracking;
using ConPersistence;
using ConPersistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConWatch;

/// <summary>
/// Owns the engine lifecycle: seed config at startup, build the engine from
/// DB config, attach the recorder and collector, and rebuild (debounced) when
/// the configuration changes.
/// </summary>
public class WatcherHostService(
    IDbContextFactory<ConWatchDbContext> dbFactory,
    ConfigStore store,
    ConfigService configService,
    SettingsStore settingsStore,
    IPingTransceiverFactory pingFactory,
    IDomainNameResolverFactory resolverFactory,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    IOptions<VerdictOptions> verdictOptions,
    IOptions<PersistenceOptions> persistenceOptions,
    IConfiguration configuration,
    TransitionRecorder recorder,
    GlitchRecorder glitchRecorder,
    RollupCollector collector,
    StatusBroadcaster broadcaster,
    Metrics.MetricsCounters metricsCounters,
    ConNotify.MqttNotifier mqttNotifier,
    ConNotify.WebhookNotifier webhookNotifier) : IHostedService
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

        // Schema migration happened in DatabaseStartup.PrepareAsync before any
        // hosted service started; this only seeds an empty config.
        if (await store.IsEmptyAsync())
        {
            var seed = configuration.GetSection(WatcherOptions.SectionName).Exists()
                ? configuration.GetSection(WatcherOptions.SectionName).Get<WatcherOptions>()
                : null;
            await store.SeedAsync(seed);
        }

        // Appsettings values fill any settings keys not yet in the DB (upgrade
        // path); the table is authoritative from then on.
        var seedDefaults = new Dictionary<string, string>
        {
            [SettingKeys.WeatherThreshold] = verdictOptions.Value.WeatherThreshold.ToString(),
            [SettingKeys.MinuteRetentionDays] = persistenceOptions.Value.MinuteRetentionDays.ToString(),
            [SettingKeys.HourlyRetentionMonths] = persistenceOptions.Value.HourlyRetentionMonths.ToString(),
            [SettingKeys.TransitionRetentionMonths] = persistenceOptions.Value.TransitionRetentionMonths.ToString(),
            [SettingKeys.RetentionRunHourUtc] = persistenceOptions.Value.RetentionRunHourUtc.ToString(),
            [SettingKeys.WarmRestartMaxGapMinutes] = "10",
        };
        if (verdictOptions.Value.AnchorPool is { Length: > 0 } anchorPool)
            seedDefaults[SettingKeys.AnchorPool] = anchorPool;
        await settingsStore.SeedMissingAsync(seedDefaults);

        await StartEngineAsync();
        configService.ConfigChanged += OnConfigChanged;
        settingsStore.SettingsChanged += OnConfigChanged;
        _log.LogInformation("WatcherHostService started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        configService.ConfigChanged -= OnConfigChanged;
        settingsStore.SettingsChanged -= OnConfigChanged;
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

    /// <summary>True when the master enable switch says monitor (the default).</summary>
    public async Task<bool> EngineEnabledAsync() =>
        await settingsStore.GetAsync(SettingKeys.EngineEnabled) != "false";

    private async Task StartEngineAsync(EngineSeed? seed = null)
    {
        if (!await EngineEnabledAsync())
        {
            // Archive mode: no engine, but everything recorded so far must be
            // on disk — a take-over snapshots this node right after disabling.
            await recorder.FlushAsync();
            await glitchRecorder.FlushAsync();
            await collector.FlushAllAsync();
            _log.LogInformation("Monitoring disabled (Engine.Enabled=false); serving as archive");
            return;
        }

        var options = await store.LoadWatcherOptionsAsync();
        var validation = new WatcherOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            // Should be unreachable: every write was pre-validated.
            _log.LogError("Stored configuration is invalid; engine not started: {Failures}",
                validation.FailureMessage);
            return;
        }

        seed ??= await BuildSeedFromDbAsync();

        var effectiveVerdict = new VerdictOptions
        {
            AnchorPool = await settingsStore.GetAsync(SettingKeys.AnchorPool),
            WeatherThreshold = await settingsStore.GetIntAsync(SettingKeys.WeatherThreshold,
                verdictOptions.Value.WeatherThreshold),
        };
        var engine = new WatcherEngine(timeProvider, pingFactory, resolverFactory,
            loggerFactory, Options.Create(options), Options.Create(effectiveVerdict));
        recorder.Attach(engine.TransitionSource);
        glitchRecorder.Attach(engine.GlitchSource);
        collector.Attach(engine.PingSampleSource);
        broadcaster.Attach(engine);
        metricsCounters.Attach(engine);
        mqttNotifier.Attach(engine);
        webhookNotifier.Attach(engine);
        await engine.StartAsync(_appStopping, seed);
        Engine = engine;
        _log.LogInformation("Engine started with {Count} targets{Warm}",
            options.Targets.Count, seed is { IsEmpty: false } ? " (warm start)" : "");
    }

    private async Task<bool> WarmRestartEnabledAsync() =>
        await settingsStore.GetIntAsync(SettingKeys.WarmRestartMaxGapMinutes, 10) > 0;

    /// <summary>
    /// Presumed states from the last recorded transitions, gated by freshness:
    /// only when the watcher was last alive within WarmRestartMaxGapMinutes.
    /// HistoryReset rows (repointed targets) and undeterminable states never
    /// seed; an Unreachable seed needs an ancestor seeded Down to justify it.
    /// </summary>
    private async Task<EngineSeed?> BuildSeedFromDbAsync()
    {
        var maxGapMinutes = await settingsStore.GetIntAsync(SettingKeys.WarmRestartMaxGapMinutes, 10);
        if (maxGapMinutes <= 0)
            return null;

        await using var db = await dbFactory.CreateDbContextAsync();
        var lastAlive = await WarmStartQueries.LastAliveUtcAsync(db);
        if (lastAlive is null)
            return null;
        var gap = timeProvider.GetUtcNow().UtcDateTime - lastAlive.Value;
        if (gap > TimeSpan.FromMinutes(maxGapMinutes))
        {
            _log.LogInformation(
                "Cold start: watcher was last alive {Gap} ago (warm-restart window is {Max} min)",
                gap, maxGapMinutes);
            return null;
        }

        var options = await store.LoadWatcherOptionsAsync();
        var targetsById = options.Targets.Where(t => t.Id is not null)
            .ToDictionary(t => t.Id!.Value);
        var latest = await WarmStartQueries.LatestTransitionsPerSubjectAsync(db);

        var seed = new EngineSeed();
        foreach (var row in latest)
        {
            if (row.Trigger == "HistoryReset")
                continue;
            switch (row.SubjectKind)
            {
                case "Target" when targetsById.TryGetValue(row.SubjectId, out var target)
                    && Enum.TryParse<HostState>(row.ToState, out var state)
                    && state is not (HostState.Initializing or HostState.Starting):
                    seed.Targets[row.SubjectId] = new TargetSeed(state, target.Address, target.Probe);
                    break;
                case "Pool" when Enum.TryParse<HostState>(row.ToState, out var poolState)
                    && poolState is not (HostState.Initializing or HostState.Starting):
                    seed.Pools[row.SubjectId] = poolState;
                    break;
            }
        }
        var verdictRow = latest.FirstOrDefault(r => r.SubjectKind == "Verdict"
            && r.Trigger != "HistoryReset");
        if (verdictRow?.Verdict is { } verdictName &&
            Enum.TryParse<ConEngine.Topology.VerdictState>(verdictName, out var verdict) &&
            verdict != ConEngine.Topology.VerdictState.Unknown)
        {
            seed = new EngineSeed { Targets = seed.Targets, Pools = seed.Pools, Verdict = verdict };
        }

        DropUnjustifiedUnreachable(seed, targetsById);
        if (seed.IsEmpty)
            return null;
        _log.LogInformation(
            "Warm restart: last alive {Gap} ago; seeding {Targets} target(s), {Pools} pool(s), verdict {Verdict}",
            gap, seed.Targets.Count, seed.Pools.Count, seed.Verdict?.ToString() ?? "unseeded");
        return seed;
    }

    /// <summary>An Unreachable presumption is only coherent behind a Down
    /// ancestor; without one, that target cold-starts instead. Config links
    /// parents by name, so the walk goes name-by-name (depth-capped).</summary>
    private static void DropUnjustifiedUnreachable(EngineSeed seed,
        IReadOnlyDictionary<Guid, TargetConfig> targetsById)
    {
        var byName = new Dictionary<string, (Guid Id, TargetConfig Config)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (id, config) in targetsById)
            byName[config.Name] = (id, config);

        foreach (var (id, targetSeed) in seed.Targets.ToList())
        {
            if (targetSeed.State != HostState.Unreachable)
                continue;
            var justified = false;
            var parentName = targetsById.TryGetValue(id, out var t) ? t.Parent : null;
            for (var depth = 0; depth < 20 && parentName is { Length: > 0 }; depth++)
            {
                if (!byName.TryGetValue(parentName, out var ancestor))
                    break;
                if (seed.Targets.TryGetValue(ancestor.Id, out var ancestorSeed) &&
                    ancestorSeed.State == HostState.Down)
                {
                    justified = true;
                    break;
                }
                parentName = ancestor.Config.Parent;
            }
            if (!justified)
                seed.Targets.Remove(id);
        }
    }

    /// <summary>Presumed states carried over from the outgoing engine on a
    /// config rebuild (fresh by definition — no gap gate). Addresses ride
    /// along so a repointed target cold-starts.</summary>
    private static EngineSeed BuildSeedFromEngine(WatcherEngine engine)
    {
        var seed = new EngineSeed
        {
            Verdict = engine.Verdict is { CurrentVerdict: not ConEngine.Topology.VerdictState.Unknown } v
                ? v.CurrentVerdict : null,
        };
        foreach (var (id, monitor) in engine.Monitors)
        {
            if (monitor.CurrentState is HostState.Initializing or HostState.Starting)
                continue;
            seed.Targets[id] = new TargetSeed(monitor.CurrentState, monitor.Target.Address, monitor.Target.Probe);
        }
        foreach (var pool in engine.PoolCoordinators)
        {
            if (pool.CurrentState is not (HostState.Initializing or HostState.Starting))
                seed.Pools[pool.Pool.Id] = pool.CurrentState;
        }
        return seed;
    }

    /// <summary>Stops the engine and rebuilds it from current DB config.</summary>
    public async Task RebuildAsync()
    {
        await _rebuildLock.WaitAsync();
        try
        {
            _log.LogInformation("Rebuilding engine after config change");
            EngineSeed? seed = null;
            if (Engine is { } engine)
            {
                Engine = null;
                // Carry the outgoing engine's observed states into the new one
                // (fresh by definition) so a config edit doesn't re-measure the
                // world; the DB-seed path stays as fallback via StartEngineAsync.
                if (await WarmRestartEnabledAsync())
                    seed = BuildSeedFromEngine(engine);
                await engine.StopAsync();
            }
            await StartEngineAsync(seed);
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
