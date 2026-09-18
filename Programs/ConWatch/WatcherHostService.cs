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
    IHostApplicationLifetime lifetime,
    TransitionRecorder recorder,
    GlitchRecorder glitchRecorder,
    RollupCollector collector,
    StatusBroadcaster broadcaster,
    IEnumerable<IEngineObserver> observers) : IHostedService
{
    private readonly ILogger<WatcherHostService> _log = loggerFactory.CreateLogger<WatcherHostService>();
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private readonly Lock _debounceLock = new();
    private CancellationTokenSource? _debounceCts;
    // The host's real shutdown signal (StartAsync's token only covers startup).
    private readonly CancellationToken _appStopping = lifetime.ApplicationStopping;
    // Set under _rebuildLock by StopAsync: a rebuild queued behind it must not
    // start a new engine after shutdown.
    private bool _stopped;
    private int _rebuildCount;
    // Generation of the most recent config/settings write OnConfigChanged
    // observed, and the highest generation an actual rebuild has reflected.
    // RebuildAsync's skip check compares the two so a direct call correctly
    // supersedes — rather than merely races — a debounced rebuild scheduled
    // for the very same write: with RebuildDebounceSeconds at 0 (tests), the
    // debounced Task.Run can finish before the direct caller even reaches
    // its own Cancel() call, so cancellation-timing alone can't make this
    // deterministic; comparing against a generation captured from the
    // shared write event, by both sides, can. Both guarded by _debounceLock.
    private long _currentGeneration;
    private long _servicedGeneration;

    /// <summary>Rebuilds that actually ran (not the ones refused after stop). Test-visible.</summary>
    public int RebuildCount => Volatile.Read(ref _rebuildCount);

    /// <summary>The running engine, or null before start / mid-rebuild.</summary>
    public WatcherEngine? Engine { get; private set; }

    public bool EngineRunning => Engine is not null;

    /// <summary>The master enable switch as last read by StartEngineAsync; the
    /// health, status and metrics endpoints read this instead of the database.</summary>
    public bool EngineEnabled { get; private set; } = true;

    private void DetachObservers()
    {
        foreach (var observer in observers)
            observer.Detach();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
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
        lock (_debounceLock) { _debounceCts?.Cancel(); _debounceCts?.Dispose(); _debounceCts = null; }
        // Serialize with any in-flight rebuild: two concurrent engine.StopAsync
        // calls race inside the coordinators' Dispose.
        await _rebuildLock.WaitAsync(CancellationToken.None);
        try
        {
            _stopped = true;
            if (Engine is { } engine)
            {
                Engine = null;
                await engine.StopAsync();
                DetachObservers();
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
        EngineEnabled = await EngineEnabledAsync();
        if (!EngineEnabled)
        {
            // Archive mode: no engine, but everything recorded so far must be
            // on disk — a take-over snapshots this node right after disabling.
            DetachObservers();
            await broadcaster.PushDisabledAsync();
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
        foreach (var observer in observers)
            observer.Attach(engine);
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

    /// <summary>
    /// Stops the engine and rebuilds it from current DB config. A direct call
    /// supersedes any debounced rebuild still pending or already under way
    /// for the same settings write, so an Engine.Enabled toggle rebuilds
    /// once, not twice: it's a no-op (still returns true — the settings are
    /// already reflected) once a rebuild has already serviced the generation
    /// this call targets. Returns false when the rebuild itself failed (the
    /// caller decides whether that is a 500).
    /// </summary>
    public Task<bool> RebuildAsync() => RebuildInternalAsync(allowSkip: true);

    /// <summary>
    /// Same as <see cref="RebuildAsync"/>, but always performs the rebuild
    /// instead of treating it as already covered. For callers whose
    /// triggering write doesn't flow through OnConfigChanged — factory
    /// reset's raw table deletes, the snapshot importer clone and take-over
    /// use (S7: it bypasses the DI ConfigService singleton) — the generation
    /// check can't tell "a rebuild already reflects my write" from "stale",
    /// so skipping would risk leaving the engine running against data that
    /// no longer matches the database.
    /// </summary>
    public Task<bool> ForceRebuildAsync() => RebuildInternalAsync(allowSkip: false);

    private async Task<bool> RebuildInternalAsync(bool allowSkip)
    {
        long targetGeneration;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            targetGeneration = _currentGeneration;
        }
        await _rebuildLock.WaitAsync();
        try
        {
            if (_stopped)
            {
                _log.LogDebug("Rebuild requested after shutdown; ignored");
                return false;
            }
            if (allowSkip && targetGeneration <= Volatile.Read(ref _servicedGeneration))
            {
                _log.LogDebug("A rebuild already reflects the current settings; skipped");
                return true;
            }
            Interlocked.Increment(ref _rebuildCount);
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
                DetachObservers();
            }
            await StartEngineAsync(seed);
            // Covers StartEngineAsync's other early return (config validation
            // failed) as well as the archive branch (which already detached
            // itself) — Detach() is idempotent, so a harmless double call
            // there just guarantees no observer is left attached whenever the
            // rebuild didn't end with a running engine.
            if (Engine is null)
                DetachObservers();
            // The only writer: always inside _rebuildLock.
            if (targetGeneration > _servicedGeneration)
                Volatile.Write(ref _servicedGeneration, targetGeneration);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Engine rebuild failed");
            return false;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private void OnConfigChanged()
    {
        CancellationTokenSource cts;
        lock (_debounceLock)
        {
            _currentGeneration++;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            cts = CancellationTokenSource.CreateLinkedTokenSource(_appStopping);
            _debounceCts = cts;
        }
        // Settings can flip the master switch; the endpoints must not report
        // the old value while the rebuild debounce runs. Read it synchronously
        // here rather than inside the Task.Run below: SetManyAsync awaits its
        // write before raising this event, so this line runs on the caller's
        // own async call stack (before the PUT/config-commit responds) — a
        // fire-and-forget Task.Run would race that response under load
        // instead of reliably beating it.
        EngineEnabled = EngineEnabledAsync().GetAwaiter().GetResult();
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
