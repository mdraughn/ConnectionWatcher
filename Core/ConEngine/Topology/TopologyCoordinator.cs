using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>
/// Reads the reachability tree from evidence (spec 2026-10-09 §4.4–4.5).
/// A target is never silenced by its ancestors: it is Unreachable only when
/// its OWN probes show it Down and an ancestor is Down with nothing beyond it
/// answering. The shallowest such ancestor is the root cause. When a hop
/// starts failing or recovers, everything beyond it re-checks at once, so a
/// hop whose probe lies is contradicted in seconds.
/// </summary>
public class TopologyCoordinator : IDisposable
{
    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _monitors;
    private readonly ILogger<TopologyCoordinator> _log;
    private readonly List<IDisposable> _subscriptions = new();
    // Monitored ancestors per target, root first; monitored descendants per target.
    private readonly Dictionary<Guid, List<Guid>> _ancestors = new();
    private readonly Dictionary<Guid, List<Guid>> _descendants = new();
    private int _recomputing;
    private int _dirty;

    public TopologyCoordinator(TargetGraph graph,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors,
        ILogger<TopologyCoordinator> log)
    {
        _monitors = monitors;
        _log = log;
        foreach (var id in monitors.Keys)
        {
            var chain = new List<Guid>();
            var parent = graph.Targets[id].ParentId;
            while (parent is { } pid)
            {
                if (monitors.ContainsKey(pid))
                    chain.Add(pid);
                parent = graph.Targets[pid].ParentId;
            }
            chain.Reverse();
            _ancestors[id] = chain;
            _descendants[id] = graph.GetDescendants(id).Select(d => d.Id).Where(monitors.ContainsKey).ToList();
        }
    }

    /// <summary>The state machine saw replies in its window.</summary>
    public static bool Answering(HostState state) =>
        state is HostState.Responding or HostState.Faltering or HostState.Recovering;

    /// <summary>Something beyond this target answers: packets get through it,
    /// whatever its own probe says.</summary>
    public bool ProvenForwarding(Guid targetId) =>
        _descendants.TryGetValue(targetId, out var beyond) &&
        beyond.Any(id => Answering(_monitors[id].CurrentState));

    /// <summary>The shallowest ancestor that is Down with no proven forwarding, or null.</summary>
    public Guid? RootCause(Guid targetId)
    {
        if (!_ancestors.TryGetValue(targetId, out var chain))
            return null;
        foreach (var ancestor in chain)
        {
            if (_monitors[ancestor].CurrentState == HostState.Down && !ProvenForwarding(ancestor))
                return ancestor;
        }
        return null;
    }

    /// <summary>Subscribes to all monitors and settles every explanation once
    /// (a warm start may have seeded Unreachable presumptions).</summary>
    public void Start()
    {
        foreach (var monitor in _monitors.Values)
            _subscriptions.Add(monitor.TransitionSource.Subscribe(OnTransition));
        Recompute();
    }

    private void OnTransition(TargetTransition transition)
    {
        if (_descendants.TryGetValue(transition.TargetId, out var beyond) && beyond.Count > 0)
        {
            bool failing = transition.To is HostState.Faltering or HostState.Down;
            bool recovering = transition.From is HostState.Down or HostState.Faltering &&
                              transition.To is HostState.Recovering or HostState.Responding;
            if (failing || recovering)
            {
                // Reach beyond NOW (D5): its evidence decides what this hop's state means.
                foreach (var id in beyond)
                    _monitors[id].Discipline.ForceBurst();
            }
            if (transition.From == HostState.Down && recovering)
            {
                // Recompute releases (and bursts) what it explained. Releasing
                // here in a loop is unsafe: the first Release publishes, which
                // re-enters Recompute, which would withdraw the remaining
                // explanations (the cause is no longer Down) before the loop
                // reached them, leaving them Down instead of re-proving.
                _log.LogInformation("{Target} recovered; releasing what it explained", transition.TargetName);
            }
        }
        Recompute();
    }

    /// <summary>
    /// Applies the current root cause to one monitor. A target explained by an
    /// ancestor that has since left Down is released to re-prove from Starting
    /// (its window is from the outage); one whose cause is still Down but now
    /// proven to forward simply loses the explanation and shows its own Down.
    /// </summary>
    private void Settle(Guid id, TargetMonitor monitor)
    {
        var root = RootCause(id);
        if (root is null && monitor.ExplainedBy is { } cause &&
            _monitors.TryGetValue(cause, out var causeMonitor) &&
            causeMonitor.CurrentState != HostState.Down)
        {
            monitor.Release();
            monitor.Discipline.ForceBurst();
            return;
        }
        monitor.SetExplanation(root);
    }

    /// <summary>
    /// One thread at a time recomputes; a request that arrives meanwhile (from
    /// another thread, or re-entrantly from a transition this pass published)
    /// marks the pass dirty and returns, and the running thread goes round
    /// again. No lock is held while monitors publish.
    /// </summary>
    private void Recompute()
    {
        Interlocked.Exchange(ref _dirty, 1);
        while (Interlocked.CompareExchange(ref _recomputing, 1, 0) == 0)
        {
            try
            {
                while (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    foreach (var (id, monitor) in _monitors)
                        Settle(id, monitor);
                }
            }
            finally
            {
                // Full fence, not Volatile.Write: the _dirty re-check below must not be reordered before this release, or a request made in between is lost.
                Interlocked.Exchange(ref _recomputing, 0);
            }
            if (Volatile.Read(ref _dirty) == 0)
                return;
        }
    }

    private readonly Lock _disposeLock = new();

    public void Dispose()
    {
        List<IDisposable> toDispose;
        lock (_disposeLock)
        {
            toDispose = new List<IDisposable>(_subscriptions);
            _subscriptions.Clear();
        }
        foreach (var s in toDispose)
            s.Dispose();
        GC.SuppressFinalize(this);
    }
}
