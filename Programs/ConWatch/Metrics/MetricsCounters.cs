using System.Collections.Concurrent;
using ConEngine;
using ConEngine.Events;
using ConEngine.Topology;

namespace ConWatch.Metrics;

/// <summary>
/// Monotonic event counters for the /metrics scrape. The engine's sliding
/// windows forget, but Prometheus rate() needs totals that only ever grow,
/// so this singleton rides the transition and glitch streams the way the
/// recorders do. Totals survive engine rebuilds and reset only with the
/// process — which rate() is built to absorb.
/// </summary>
public class MetricsCounters : IEngineObserver
{
    private readonly Lock _lock = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ConcurrentDictionary<string, long> _transitions = new();
    private long _glitches;

    /// <summary>Atomic snapshot: LINQ straight over the live dictionary can
    /// throw when a transition lands mid-copy; ToArray here cannot.</summary>
    public KeyValuePair<string, long>[] TransitionsSnapshot() => _transitions.ToArray();

    public long Glitches => Interlocked.Read(ref _glitches);

    /// <summary>Subscribes to a (re)started engine, replacing any prior
    /// subscription; the accumulated totals carry across rebuilds.</summary>
    public void Attach(WatcherEngine engine)
    {
        lock (_lock)
        {
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
            _subscriptions.Add(engine.TransitionSource.Subscribe(t =>
                _transitions.AddOrUpdate(KeyOf(t), 1, (_, n) => n + 1)));
            _subscriptions.Add(engine.GlitchSource.Subscribe(_ =>
                Interlocked.Increment(ref _glitches)));
        }
    }

    private static string KeyOf(TargetTransition t) =>
        t.Kind == TransitionSubjectKind.Verdict ? VerdictCoordinator.MetricsName : t.TargetName;

    /// <summary>Drops the subscription; the accumulated totals are process-lifetime
    /// counters and are kept.</summary>
    public void Detach()
    {
        lock (_lock)
        {
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
