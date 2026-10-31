using ConCore;
using ConCore.Targets;
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
    ConInfrastructure.NameResolution.DnsLookupNames lookupNames,
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
    // A rebuild that threw leaves the node engine-less; nothing else would
    // bring it back before the next config write. Retry on these delays
    // (the last repeats) until one succeeds. _lastRebuildThrew and
    // _retryAttempt are guarded by _rebuildLock. Test seam: internal so the
    // test project can shorten the schedule instead of waiting real seconds.
    internal static TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private bool _lastRebuildThrew;
    private int _retryAttempt;

    /// <summary>Rebuilds that actually ran (not the ones refused after stop). Test-visible.</summary>
    public int RebuildCount => Volatile.Read(ref _rebuildCount);

    /// <summary>The running engine, or null before start / mid-rebuild.</summary>
    public WatcherEngine? Engine { get; private set; }

    internal bool EngineRunning => Engine is not null;

    /// <summary>The master enable switch as last read by StartEngineAsync; the
    /// health, status and metrics endpoints read this instead of the database.</summary>
    public bool EngineEnabled { get; private set; } = true;

    /// <summary>The validator's complaint from the last start attempt that
    /// refused to build an engine, or null when the last attempt got past
    /// validation. The health endpoint reports it as "ConfigInvalid".</summary>
    public string? LastStartFailure { get; private set; }

    /// <summary>
    /// Detaches every observer; never throws. One observer's failure is logged
    /// and the rest still detach — it runs in cleanup paths where a throw would
    /// skip them and, in a finally, the release of <c>_rebuildLock</c>.
    /// </summary>
    private void DetachObservers()
    {
        foreach (var observer in observers)
        {
            try
            {
                observer.Detach();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Observer {Observer} failed to detach", observer.GetType().Name);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Schema migration happened in DatabaseStartup.PrepareAsync before any
        // hosted service started; this only seeds an empty config, then makes
        // sure every built-in preset exists (an older database gains Path).
        if (await store.IsEmptyAsync())
        {
            var seed = configuration.GetSection(WatcherOptions.SectionName).Exists()
                ? configuration.GetSection(WatcherOptions.SectionName).Get<WatcherOptions>()
                : null;
            await store.SeedAsync(seed);
        }
        await store.EnsureBuiltInsAsync();

        // Appsettings values fill any settings keys not yet in the DB (upgrade
        // path); the table is authoritative from then on. Factory reset seeds
        // the same list.
        await settingsStore.SeedMissingAsync(
            SettingsDefaults.For(verdictOptions.Value, persistenceOptions.Value));

        await StartEngineAsync();
        configService.ConfigChanged += OnConfigChanged;
        settingsStore.SettingsChanged += OnSettingsChanged;
        _log.LogInformation("WatcherHostService started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        configService.ConfigChanged -= OnConfigChanged;
        settingsStore.SettingsChanged -= OnSettingsChanged;
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
    private async Task<bool> EngineEnabledAsync() =>
        await settingsStore.GetBoolAsync(SettingKeys.EngineEnabled);

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

        // The DNS probes ask for the configured lookup names plus every hostname
        // target's name (the resolver has to resolve those anyway). Refreshed
        // here, before the engine that will use them exists; empty when the
        // watch is off, so a stray resolver target reports "NoLookupNames".
        var dnsEnabled = await settingsStore.GetBoolAsync(SettingKeys.DnsEnabled);
        lookupNames.Replace(dnsEnabled
            ? ConCore.Dns.DnsLookupName.FromJson(await settingsStore.GetAsync(SettingKeys.DnsLookupNames))
                .Concat(options.Targets
                    .Where(t => t.Enabled && t.Scope != TargetScope.Resolver && !System.Net.IPAddress.TryParse(t.Address, out _))
                    .Select(t => new ConCore.Dns.DnsLookupName(t.Address, [])))
                .DistinctBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : []);

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
            DnsWatch = dnsEnabled && lookupNames.Current.Count > 0,
        };
        var engine = new WatcherEngine(timeProvider, pingFactory, resolverFactory,
            loggerFactory, Options.Create(options), Options.Create(effectiveVerdict));
        foreach (var observer in observers)
            observer.Attach(engine);
        await engine.StartAsync(_appStopping, seed);
        foreach (var observer in observers)
            observer.Started(engine);
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
            _lastRebuildThrew = false;
            _retryAttempt = 0;
            return started;
        }
        catch (Exception ex)
        {
            var delay = RetryDelays[Math.Min(_retryAttempt++, RetryDelays.Length - 1)];
            _lastRebuildThrew = true;
            _log.LogError(ex, "Engine rebuild failed; retrying in {Delay}", delay);
            ScheduleRetry(delay);
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
            try
            {
                if (Engine is null)
                    DetachObservers();
            }
            finally
            {
                _rebuildLock.Release();
            }
        }
    }

    /// <summary>
    /// Runs one unconditional rebuild after <paramref name="delay"/> unless a
    /// rebuild has succeeded meanwhile (a config write can beat the timer) or
    /// the host is stopping. A retry that throws schedules the next one.
    /// </summary>
    private void ScheduleRetry(TimeSpan delay) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, timeProvider, _appStopping);
                await _rebuildLock.WaitAsync(_appStopping);
                bool stillFailed;
                try { stillFailed = _lastRebuildThrew && !_stopped; }
                finally { _rebuildLock.Release(); }
                if (stillFailed)
                    await RebuildAsync();
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }, CancellationToken.None);

    private void OnSettingsChanged(IReadOnlyDictionary<string, string?> written)
    {
        // Settings can flip the master switch; the endpoints must not report
        // the old value while the rebuild debounce runs. The write carries the
        // new value, so set it here — on the writer's own call stack, before the
        // PUT responds — without reading it back. (It used to be read back with
        // a blocking GetResult() on the request thread, on every config write
        // too; under load that starved the thread pool.) Null removes the row,
        // which reads as enabled — the same rule as EngineEnabledAsync.
        if (written.TryGetValue(SettingKeys.EngineEnabled, out var enabled))
            EngineEnabled = SettingRules.ReadFlag(SettingKeys.EngineEnabled, enabled);
        OnConfigChanged();
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
