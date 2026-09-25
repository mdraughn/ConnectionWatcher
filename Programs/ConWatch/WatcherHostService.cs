using ConCore;
using ConEngine;
using ConEngine.Configuration;
using ConEngine.Tracking;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Recording;
using ConPersistence.Settings;
using Microsoft.Extensions.Options;

namespace ConWatch;

/// <summary>
/// Owns the engine lifecycle: seed config at startup, build the engine from
/// DB config, attach the observers, and rebuild (debounced) when the
/// configuration changes. Warm-start policy lives in <see cref="WarmStartSeeder"/>.
/// </summary>
public class WatcherHostService(
    ConfigStore store,
    ConfigService configService,
    SettingsStore settingsStore,
    WarmStartSeeder seeder,
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
    // observed (guarded by _debounceLock), and the highest generation an
    // actual rebuild has reflected (guarded by _rebuildLock — written only
    // from inside RebuildInternalAsync's held lock, so a plain read/write
    // suffices there). RebuildIfStaleAsync's skip check compares the two so
    // it correctly supersedes — rather than merely races — a debounced
    // rebuild scheduled for the very same write: with RebuildDebounceSeconds
    // at 0 (tests), the debounced Task.Run can finish before a direct caller
    // even reaches its own Cancel() call, so cancellation-timing alone can't
    // make this deterministic; comparing against a generation captured from
    // the shared write event, by both sides, can.
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

    /// <summary>The validator's complaint from the last start attempt that
    /// refused to build an engine, or null when the last attempt got past
    /// validation. The health endpoint reports it as "ConfigInvalid".</summary>
    public string? LastStartFailure { get; private set; }

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
        // path); the table is authoritative from then on. Factory reset seeds
        // the same list.
        await settingsStore.SeedMissingAsync(
            SettingsDefaults.For(verdictOptions.Value, persistenceOptions.Value));

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

    /// <summary>
    /// Brings the node to the state the settings ask for. Returns true when
    /// that succeeded — an engine is running, or archive mode was entered —
    /// and false only when the stored configuration failed validation, which
    /// leaves the node enabled but engine-less (see
    /// <see cref="LastStartFailure"/>).
    /// </summary>
    private async Task<bool> StartEngineAsync(EngineSeed? seed = null)
    {
        EngineEnabled = await EngineEnabledAsync();
        // Every attempt that gets this far starts clean; only the validation
        // branch below records a failure.
        LastStartFailure = null;
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
            return true;
        }

        var options = await store.LoadWatcherOptionsAsync();
        var validation = new WatcherOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            // Nearly unreachable: every write through the API was pre-validated.
            // A raw write — or an upgrade that tightened the rules — can still
            // land here, and the node must say so rather than look merely down.
            _log.LogError("Stored configuration is invalid; engine not started: {Failures}",
                validation.FailureMessage);
            LastStartFailure = validation.FailureMessage;
            return false;
        }

        seed ??= await seeder.FromDbAsync();

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
        return true;
    }

    /// <summary>
    /// Always rebuilds. Use this after a write that does not raise the
    /// config events (raw deletes, the snapshot importer).
    /// </summary>
    public Task<bool> RebuildAsync() => RebuildInternalAsync(allowSkip: false);

    /// <summary>
    /// Stops the engine and rebuilds it from current DB config, unless a
    /// rebuild has already serviced the generation this call targets — a
    /// no-op (still returns true; the settings are already reflected) in
    /// that case. A direct call this way supersedes any debounced rebuild
    /// still pending or already under way for the same settings write, so
    /// an Engine.Enabled toggle rebuilds once, not twice. Only safe for
    /// callers whose triggering write raised OnConfigChanged (SetManyAsync,
    /// ConfigService) — use <see cref="RebuildAsync"/> otherwise. Returns
    /// false when the rebuild itself failed (the caller decides whether
    /// that is a 500).
    /// </summary>
    public Task<bool> RebuildIfStaleAsync() => RebuildInternalAsync(allowSkip: true);

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
            if (allowSkip && targetGeneration <= _servicedGeneration)
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
                if (await seeder.EnabledAsync())
                    seed = WarmStartSeeder.FromEngine(engine);
                await engine.StopAsync();
                DetachObservers();
            }
            var started = await StartEngineAsync(seed);
            // Always inside _rebuildLock: a plain read/write is enough.
            if (targetGeneration > _servicedGeneration)
                _servicedGeneration = targetGeneration;
            return started;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Engine rebuild failed");
            return false;
        }
        finally
        {
            // Covers StartEngineAsync's validation-failed return and the
            // archive branch (which already detached itself), and also a
            // throw from StartEngineAsync after observers were attached —
            // Detach() is idempotent, so the harmless double call just
            // guarantees no observer is left attached whenever the rebuild
            // didn't end with a running engine.
            if (Engine is null)
                DetachObservers();
            _rebuildLock.Release();
        }
    }

    private void OnConfigChanged()
    {
        CancellationToken token;
        lock (_debounceLock)
        {
            _currentGeneration++;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_appStopping);
            _debounceCts = cts;
            // Captured under the lock, not read from cts later: StopAsync
            // can Cancel() + Dispose() this same cts concurrently, and
            // reading .Token off a disposed CancellationTokenSource throws
            // ObjectDisposedException.
            token = cts.Token;
        }
        // Settings can flip the master switch; the endpoints must not report
        // the old value while the rebuild debounce runs. Read it synchronously
        // here rather than inside the Task.Run below: SetManyAsync awaits its
        // write before raising this event, so this line runs on the caller's
        // own async call stack (before the PUT/config-commit responds) — a
        // fire-and-forget Task.Run would race that response under load
        // instead of reliably beating it.
        // Guarded: this runs on the settings writer's stack after the write
        // already committed, so a transient read failure must not turn a
        // committed PUT into a 500 — nor skip the rebuild scheduled below,
        // which reads the switch again anyway.
        try
        {
            EngineEnabled = EngineEnabledAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not refresh the master switch after a settings change; the rebuild will read it");
        }
        var delay = TimeSpan.FromSeconds(persistenceOptions.Value.RebuildDebounceSeconds);
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, timeProvider, token);
                await RebuildIfStaleAsync();
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer change or shutdown.
            }
        }, CancellationToken.None);
    }
}
