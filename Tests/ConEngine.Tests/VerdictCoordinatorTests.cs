using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class VerdictCoordinatorTests
{
    private static readonly PingProfile Fast = new()
    {
        Name = "Fast", HistorySize = 20, MinUsableSample = 5,
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-08-16T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _topology;
    private readonly VerdictCoordinator _verdict;
    private readonly List<TargetTransition> _verdictTransitions = new();

    public VerdictCoordinatorTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal", Operator = "Comcast" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Modem" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        _topology = new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance);
        _topology.Start();
        _verdict = new VerdictCoordinator(_graph, _graph.GetPoolByName("Internet"), _monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance);
        _verdict.TransitionSource.Subscribe(_verdictTransitions.Add);
        _verdict.Start();
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

    private void AllHealthy()
    {
        foreach (var name in new[] { "Router", "Modem", "Google", "Cloudflare", "Quad9" })
            Feed(name, 5, true);
    }

    [Fact]
    public void Starts_unknown_then_healthy()
    {
        Assert.Equal(VerdictState.Unknown, _verdict.CurrentVerdict);
        AllHealthy();
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        var t = _verdictTransitions.Last();
        Assert.Equal(TransitionTrigger.VerdictChanged, t.Reason.Trigger);
        Assert.Equal("Healthy", t.Reason.Verdict);
        Assert.Equal(TransitionSubjectKind.Verdict, t.Kind);
        Assert.Equal(VerdictCoordinator.SubjectId, t.TargetId);
    }

    [Fact]
    public void Link_verdicts_name_the_operator_when_the_blamed_hop_has_one()
    {
        AllHealthy();
        Feed("Modem", 20, false); // Modem carries Operator = "Comcast"
        Assert.Equal(VerdictState.LinkDown, _verdict.CurrentVerdict);
        Assert.Equal("LinkDown: Comcast — Modem is Down", _verdict.CurrentDetail);
        Assert.Equal("Comcast", _verdict.CurrentOperator);

        Feed("Modem", 20, true);
        AllHealthy(); // released anchors re-prove from Starting
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.Null(_verdict.CurrentOperator);
    }

    [Fact]
    public void Link_verdicts_without_an_operator_keep_plain_wording()
    {
        AllHealthy();
        Feed("Router", 20, false); // Router has no operator (Mark's own gear)
        Assert.Equal(VerdictState.LinkDown, _verdict.CurrentVerdict);
        Assert.Equal("LinkDown: Router is Down", _verdict.CurrentDetail);
        Assert.Null(_verdict.CurrentOperator);
    }

    [Fact]
    public void One_anchor_down_is_site_incident()
    {
        AllHealthy();
        Feed("Google", 20, false);
        Assert.Equal(VerdictState.SiteIncident, _verdict.CurrentVerdict);
        Assert.Contains("Google", _verdict.CurrentDetail);
    }

    [Fact]
    public void Two_anchors_down_is_internet_weather()
    {
        AllHealthy();
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
        var t = _verdictTransitions.Last();
        Assert.Equal(HostState.Responding, t.From);
        Assert.Equal(HostState.Faltering, t.To);
    }

    [Fact]
    public void Ancestor_down_beats_weather()
    {
        AllHealthy();
        Feed("Modem", 20, false);
        Assert.Equal(VerdictState.LinkDown, _verdict.CurrentVerdict);
        Assert.Contains("Modem", _verdict.CurrentDetail);
    }

    [Fact]
    public void Ancestor_faltering_is_link_degraded()
    {
        AllHealthy();
        Feed("Modem", 3, false);
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Contains("Modem", _verdict.CurrentDetail);
    }

    [Fact]
    public void Recovering_anchor_still_counts_degraded()
    {
        AllHealthy();
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);

        // Google starts answering again: Down -> Recovering, still degraded.
        Feed("Google", 3, true);
        Assert.Equal(HostState.Recovering, _monitors[_graph.GetByName("Google").Id].CurrentState);
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
    }

    [Fact]
    public void Below_threshold_anchor_count_carries_limited_confidence_qualifier()
    {
        // Separate 1-anchor topology: Router -> Google, pool of just Google, threshold 2.
        var graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google"] }],
        });
        var monitors = new Dictionary<Guid, TargetMonitor>();
        foreach (var target in graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitors[target.Id] = monitor;
        }
        var verdict = new VerdictCoordinator(graph, graph.GetPoolByName("Internet"), monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance);
        verdict.Start();

        foreach (var m in monitors.Values)
            for (int i = 0; i < 5; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                m.RecordPingResult(new PingResult(m.ResolvedAddress!, true, 5, timestamp: _time.GetUtcNow()));
            }

        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        Assert.Contains("only 1 anchor", verdict.CurrentDetail);
        Assert.Contains("cannot distinguish", verdict.CurrentDetail);
    }

    /// <summary>Four anchors, three of them one operator: Router -> {MS-1, MS-2, MS-3 (Microsoft), Google}.</summary>
    private (VerdictCoordinator Verdict, Dictionary<Guid, TargetMonitor> Monitors, TargetGraph Graph) OperatorTopology()
    {
        var graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "MS-1", Address = "20.0.0.1", Parent = "Router", Operator = "Microsoft" },
                new TargetConfig { Name = "MS-2", Address = "20.0.0.2", Parent = "Router", Operator = "microsoft " },
                new TargetConfig { Name = "MS-3", Address = "20.0.0.3", Parent = "Router", Operator = "Microsoft" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["MS-1", "MS-2", "MS-3", "Google"] }],
        });
        var monitors = new Dictionary<Guid, TargetMonitor>();
        foreach (var target in graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitors[target.Id] = monitor;
        }
        var verdict = new VerdictCoordinator(graph, graph.GetPoolByName("Internet"), monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance);
        verdict.Start();
        foreach (var m in monitors.Values)
            for (int i = 0; i < 5; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                m.RecordPingResult(new PingResult(m.ResolvedAddress!, true, 5, timestamp: _time.GetUtcNow()));
            }
        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        return (verdict, monitors, graph);
    }

    private void FeedDown(Dictionary<Guid, TargetMonitor> monitors, TargetGraph graph, string name)
    {
        var monitor = monitors[graph.GetByName(name).Id];
        for (int i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, false, 0, timestamp: _time.GetUtcNow()));
        }
    }

    [Fact]
    public void Three_anchors_of_one_operator_down_is_still_a_site_incident()
    {
        var (verdict, monitors, graph) = OperatorTopology();
        FeedDown(monitors, graph, "MS-1");
        FeedDown(monitors, graph, "MS-2");
        FeedDown(monitors, graph, "MS-3");

        // Three degraded anchors, one operator: one piece of evidence, not three.
        Assert.Equal(VerdictState.SiteIncident, verdict.CurrentVerdict);
        Assert.Contains("Microsoft", verdict.CurrentDetail);
    }

    [Fact]
    public void A_second_operator_degraded_makes_it_weather()
    {
        var (verdict, monitors, graph) = OperatorTopology();
        FeedDown(monitors, graph, "MS-1");
        FeedDown(monitors, graph, "MS-2");
        FeedDown(monitors, graph, "Google"); // untagged anchors count as their own operator

        Assert.Equal(VerdictState.InternetWeather, verdict.CurrentVerdict);
        Assert.Contains("2 of 2 operators", verdict.CurrentDetail);
        Assert.Contains("3 of 4 anchors", verdict.CurrentDetail);
    }

    [Fact]
    public void At_or_above_threshold_has_no_qualifier()
    {
        AllHealthy();
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.DoesNotContain("cannot distinguish", _verdict.CurrentDetail);
    }

    [Fact]
    public void Label_change_with_same_mapped_state_still_emits()
    {
        AllHealthy();
        Feed("Google", 20, false);
        var t = _verdictTransitions.Last();
        Assert.Equal("SiteIncident", t.Reason.Verdict);
        Assert.Equal(HostState.Responding, t.From);
        Assert.Equal(HostState.Responding, t.To);
    }
}
