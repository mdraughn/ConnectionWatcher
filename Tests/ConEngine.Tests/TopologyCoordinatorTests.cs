using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class TopologyCoordinatorTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _coordinator;

    private static readonly PingProfile Fast = new()
    {
        Name = "Fast", HistorySize = 20, MinUsableSample = 5,
    };

    public TopologyCoordinatorTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
            ],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        _coordinator = new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance);
        _coordinator.Start();
    }

    private void Feed(string name, int count, bool success)
    {
        var monitor = _monitors[_graph.GetByName(name).Id];
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    private HostState State(string name) => _monitors[_graph.GetByName(name).Id].CurrentState;

    [Fact]
    public void Router_down_cascades_unreachable_to_all_descendants()
    {
        Feed("Router", 5, true);
        Feed("Modem", 5, true);
        Feed("Google", 5, true);

        Feed("Router", 20, false); // Responding -> Faltering -> Down
        Assert.Equal(HostState.Down, State("Router"));
        Assert.Equal(HostState.Unreachable, State("Modem"));
        Assert.Equal(HostState.Unreachable, State("Google"));
    }

    [Fact]
    public void Router_recovery_releases_descendants_to_starting()
    {
        Feed("Router", 5, true);
        Feed("Modem", 5, true);
        Feed("Google", 5, true);
        Feed("Router", 20, false);
        Assert.Equal(HostState.Unreachable, State("Google"));

        // Router comes back: Down -> Recovering (>= RecoveringThreshold successes).
        Feed("Router", 10, true);
        Assert.NotEqual(HostState.Down, State("Router"));
        Assert.Equal(HostState.Starting, State("Modem"));
        Assert.Equal(HostState.Starting, State("Google"));
    }

    [Fact]
    public void Released_children_burst_to_reprove_quickly()
    {
        Feed("Router", 5, true);
        Feed("Modem", 5, true);
        Feed("Google", 5, true);
        Feed("Router", 20, false);
        Assert.Equal(HostState.Unreachable, State("Google"));

        // Router recovers and the subtree is released — the children must
        // re-prove at burst cadence, not the ~100s normal interval.
        Feed("Router", 10, true);
        Assert.Equal(HostState.Starting, State("Modem"));
        Assert.True(_monitors[_graph.GetByName("Modem").Id].Discipline.IsBurstActive);
        Assert.True(_monitors[_graph.GetByName("Google").Id].Discipline.IsBurstActive);
    }

    [Fact]
    public void Mid_tree_down_only_affects_subtree()
    {
        Feed("Router", 5, true);
        Feed("Modem", 5, true);
        Feed("Google", 5, true);

        Feed("Modem", 20, false);
        Assert.Equal(HostState.Responding, State("Router"));
        Assert.Equal(HostState.Down, State("Modem"));
        Assert.Equal(HostState.Unreachable, State("Google"));
    }
}
