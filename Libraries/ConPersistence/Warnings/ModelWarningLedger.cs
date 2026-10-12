using System.Reactive.Subjects;
using ConEngine;
using ConEngine.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Warnings;

/// <summary>
/// The administrator's notes (spec 2026-10-09 §4.6) as INSTANCES: the engine
/// says which conditions hold; the ledger decides what is one note. It
/// outlives engines, so a rebuild (any config edit) continues a note rather
/// than ending and reopening it: a carried note the new engine re-raises
/// keeps its start and its acknowledgement; one it never re-raises within
/// <see cref="ResyncGrace"/> (<see cref="ProbeOnlyResyncGrace"/> for
/// ProbeOnlyLoss) is closed. Acknowledging hides an instance until
/// it clears and recurs.
/// </summary>
public class ModelWarningLedger : IEngineObserver, IHostedService
{
    /// <summary>Long enough for a new engine to re-raise every kind but
    /// ProbeOnlyLoss: the slowest is NeverAnswered (5 min).</summary>
    public static readonly TimeSpan ResyncGrace = ModelCheckCoordinator.NeverAnsweredAfter + TimeSpan.FromMinutes(1);

    /// <summary>ProbeOnlyLoss's own grace: a new engine needs 10 fresh windows
    /// to re-prove it. Only this kind waits this long, so a deleted hop's other
    /// notes do not linger.</summary>
    public static readonly TimeSpan ProbeOnlyResyncGrace = ModelCheckCoordinator.ProbeOnlyProveTime + TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<ConWatchDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<ModelWarningLedger> _log;
    private readonly Lock _lock = new();
    private readonly Subject<ModelWarningOp> _ops = new();

    // Guarded by _lock.
    private readonly Dictionary<string, ModelWarningInstance> _instances = new();
    private readonly HashSet<string> _unconfirmed = new();
    private IDisposable? _subscription;
    private ITimer? _resync;
    private ITimer? _probeOnlyResync;
    private int _generation;

    public ModelWarningLedger(ModelWarningRecorder recorder, IDbContextFactory<ConWatchDbContext> dbFactory,
        TimeProvider time, ILogger<ModelWarningLedger> log)
    {
        _dbFactory = dbFactory;
        _time = time;
        _log = log;
        recorder.Attach(_ops);
    }

    /// <summary>Active instances, oldest first.</summary>
    public IReadOnlyList<ModelWarningInstance> Current
    {
        get { lock (_lock) { return _instances.Values.OrderBy(w => w.SinceUtc).ToList(); } }
    }

    public Task StartAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Process start: open rows become carried (unconfirmed) instances.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var open = await db.ModelWarnings.Where(r => r.EndUtc == null)
            .OrderBy(r => r.StartUtc).ToListAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var r in open)
            {
                if (_instances.ContainsKey(r.Key))
                    continue; // already live (an Attach ran first): the engine owns it
                if (!Enum.TryParse<ModelWarningKind>(r.Kind, out var kind))
                    continue;
                _instances[r.Key] = new ModelWarningInstance(r.Key, kind, r.TargetId, r.TargetName, r.Message,
                    new DateTimeOffset(DateTime.SpecifyKind(r.StartUtc, DateTimeKind.Utc)),
                    r.AcknowledgedUtc is not null);
                _unconfirmed.Add(r.Key);
            }
        }
    }

    public void Attach(WatcherEngine engine) =>
        Attach(engine.ModelConditionSource, () => engine.ModelConditions);

    /// <summary>Follows one engine's conditions. Everything held so far is carried:
    /// it continues if re-raised, and closes at the grace otherwise.</summary>
    public void Attach(IObservable<ModelConditionChange> source, Func<IReadOnlyList<ModelCondition>> active)
    {
        lock (_lock)
        {
            DetachCore();
            _unconfirmed.UnionWith(_instances.Keys);
            var generation = _generation;
            _subscription = source.Subscribe(OnChange);
            var now = _time.GetUtcNow();
            foreach (var condition in active())
                Raise(condition, now);
            _resync = _time.CreateTimer(_ => Resync(generation, probeOnly: false), null,
                ResyncGrace, Timeout.InfiniteTimeSpan);
            _probeOnlyResync = _time.CreateTimer(_ => Resync(generation, probeOnly: true), null,
                ProbeOnlyResyncGrace, Timeout.InfiniteTimeSpan);
        }
    }

    public void Detach()
    {
        lock (_lock)
            DetachCore();
    }

    /// <summary>Under _lock.</summary>
    private void DetachCore()
    {
        _subscription?.Dispose();
        _subscription = null;
        _resync?.Dispose();
        _resync = null;
        _probeOnlyResync?.Dispose();
        _probeOnlyResync = null;
        _generation++; // any timer still in flight is now stale
    }

    /// <summary>Hides the instance until it clears and recurs. False when no
    /// such note is active.</summary>
    public bool Acknowledge(string key)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(key, out var w))
                return false;
            if (w.Acknowledged)
                return true;
            var acknowledged = w with { Acknowledged = true };
            _instances[key] = acknowledged;
            _ops.OnNext(new ModelWarningOp(ModelWarningOpKind.Acknowledged, acknowledged, _time.GetUtcNow()));
            _log.LogInformation("Model warning acknowledged: {Key}", key);
            return true;
        }
    }

    private void OnChange(ModelConditionChange change)
    {
        lock (_lock)
        {
            if (change.Active)
                Raise(change.Condition, change.AtUtc);
            else
                Close(change.Condition.Key, change.AtUtc);
        }
    }

    /// <summary>Under _lock.</summary>
    private void Raise(ModelCondition c, DateTimeOffset at)
    {
        _unconfirmed.Remove(c.Key);
        if (_instances.TryGetValue(c.Key, out var existing))
        {
            _instances[c.Key] = existing with { Message = c.Message, TargetName = c.TargetName }; // continuation
            return;
        }
        var w = new ModelWarningInstance(c.Key, c.Kind, c.TargetId, c.TargetName, c.Message, at, false);
        _instances[c.Key] = w;
        _ops.OnNext(new ModelWarningOp(ModelWarningOpKind.Opened, w, at));
        _log.LogInformation("Model warning raised: {Message}", c.Message);
    }

    /// <summary>Under _lock.</summary>
    private void Close(string key, DateTimeOffset at)
    {
        _unconfirmed.Remove(key);
        if (!_instances.Remove(key, out var w))
            return;
        _ops.OnNext(new ModelWarningOp(ModelWarningOpKind.Closed, w, at));
        _log.LogInformation("Model warning cleared: {Key}", key);
    }

    /// <summary>Timer thread: a grace is over; close what the engine never
    /// re-raised — every kind but ProbeOnlyLoss at <see cref="ResyncGrace"/>,
    /// whatever remains at <see cref="ProbeOnlyResyncGrace"/>. Never throws.</summary>
    private void Resync(int generation, bool probeOnly)
    {
        try
        {
            lock (_lock)
            {
                if (generation != _generation)
                    return;
                var now = _time.GetUtcNow();
                foreach (var key in _unconfirmed.ToList())
                {
                    if (probeOnly || !_instances.TryGetValue(key, out var w) || w.Kind != ModelWarningKind.ProbeOnlyLoss)
                        Close(key, now);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Model warning resync failed");
        }
    }
}
