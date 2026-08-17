using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>
/// Enforces the reachability tree: when a target goes Down its descendants
/// become Unreachable (and stop pinging); when it recovers they are released.
/// </summary>
public class TopologyCoordinator : IDisposable
{
    private readonly TargetGraph _graph;
    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _monitors;
    private readonly ILogger<TopologyCoordinator> _log;
    private readonly List<IDisposable> _subscriptions = new();

    public TopologyCoordinator(TargetGraph graph,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors,
        ILogger<TopologyCoordinator> log)
    {
        _graph = graph;
        _monitors = monitors;
        _log = log;
    }

    /// <summary>Subscribes to all monitors. Call once after monitors are created.</summary>
    public void Start()
    {
        foreach (var monitor in _monitors.Values)
            _subscriptions.Add(monitor.TransitionSource.Subscribe(OnTransition));
    }

    private void OnTransition(TargetTransition transition)
    {
        if (transition.To == HostState.Down)
        {
            _log.LogInformation("{Target} is Down; marking descendants unreachable", transition.TargetName);
            foreach (var descendant in _graph.GetDescendants(transition.TargetId))
            {
                if (_monitors.TryGetValue(descendant.Id, out var monitor))
                    monitor.SetUnreachable(transition.TargetId);
            }
        }
        else if (transition.From == HostState.Down &&
                 transition.To is HostState.Recovering or HostState.Responding)
        {
            _log.LogInformation("{Target} recovered; releasing descendants", transition.TargetName);
            foreach (var descendant in _graph.GetDescendants(transition.TargetId))
            {
                if (_monitors.TryGetValue(descendant.Id, out var monitor) &&
                    monitor.CurrentState == HostState.Unreachable)
                    monitor.Release();
            }
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
