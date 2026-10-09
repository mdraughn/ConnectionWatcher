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

    private void Fail(params string[] names)
    {
        foreach (var name in names)
            Feed(name, 20, false);
    }

    private VerdictState Verdict => _verdict.CurrentVerdict;

    [Fact]
    public void Verdict_transitions_are_named_by_the_subject_constants()
    {
        AllHealthy();

        Assert.NotEmpty(_verdictTransitions);
        Assert.All(_verdictTransitions, t =>
        {
            Assert.Equal(VerdictCoordinator.SubjectId, t.TargetId);
            Assert.Equal(VerdictCoordinator.SubjectName, t.TargetName);
        });
        // The name is on the wire: the metrics label and the MCP subject filter use it.
        Assert.Equal("Internet", VerdictCoordinator.SubjectName);
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
    public void A_down_hop_alone_is_not_an_internet_outage()
    {
        // The 2026-10-08 incident at verdict level: the anchors still answer.
        AllHealthy();
        Fail("Modem");
        Assert.Equal(HostState.Down, _monitors[_graph.GetByName("Modem").Id].CurrentState);
        Assert.Equal(VerdictState.Healthy, Verdict);
        Assert.DoesNotContain(_verdictTransitions, t => t.Reason.Verdict == "LinkDown");
    }

    [Fact]
    public void Link_verdicts_name_the_operator_when_the_blamed_hop_has_one()
    {
        AllHealthy();
        Fail("Modem", "Google", "Cloudflare", "Quad9"); // Modem carries Operator = "Comcast"
        Assert.Equal(VerdictState.LinkDown, Verdict);
        Assert.Equal("LinkDown: Comcast — Modem is Down", _verdict.CurrentDetail);
        Assert.Equal("Comcast", _verdict.CurrentOperator);

        Feed("Modem", 20, true);
        AllHealthy(); // released anchors re-prove from Starting
        Assert.Equal(VerdictState.Healthy, Verdict);
        Assert.Null(_verdict.CurrentOperator);
    }

    [Fact]
    public void Link_verdicts_without_an_operator_keep_plain_wording()
    {
        AllHealthy();
        Fail("Router", "Modem", "Google", "Cloudflare", "Quad9"); // Router: Mark's own gear, untagged
        Assert.Equal(VerdictState.LinkDown, Verdict);
        Assert.Equal("LinkDown: Router is Down", _verdict.CurrentDetail);
        Assert.Null(_verdict.CurrentOperator);
    }

    [Fact]
    public void Blame_skips_a_hop_that_traffic_passes_and_lands_after_the_last_proven_one()
    {
        AllHealthy();
        Fail("Router");                            // Router's probe fails…
        Fail("Google", "Cloudflare", "Quad9");     // …and the anchors fail, but Modem still answers
        Assert.Equal(VerdictState.LinkDown, Verdict);
        Assert.Equal("LinkDown: Comcast — nothing answers beyond Modem", _verdict.CurrentDetail);
        Assert.Equal("Comcast", _verdict.CurrentOperator);
    }

    [Fact]
    public void A_faltering_hop_with_healthy_anchors_is_still_healthy()
    {
        AllHealthy();
        Feed("Modem", 3, false);
        Assert.Equal(HostState.Faltering, _monitors[_graph.GetByName("Modem").Id].CurrentState);
        Assert.Equal(VerdictState.Healthy, Verdict);
    }

    [Fact]
    public void Weather_shared_by_a_faltering_path_hop_is_link_degraded()
    {
        AllHealthy();
        Fail("Google", "Cloudflare");   // weather level (threshold 2)…
        Feed("Modem", 3, false);        // …and our own hop is losing too
        Assert.Equal(VerdictState.LinkDegraded, Verdict);
        Assert.Contains("Modem", _verdict.CurrentDetail);
    }

    [Fact]
    public void One_off_net_anchor_answering_proves_the_internet_is_up()
    {
        AllHealthy();
        Fail("Google", "Cloudflare");   // Quad9 still answers; the ISP (Comcast) is known
        Assert.NotEqual(VerdictState.LinkDown, Verdict);
        Assert.NotEqual(VerdictState.LinkDegraded, Verdict);
        Assert.Equal(VerdictState.InternetWeather, Verdict);
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

        foreach (var m in monitors.Values.Where(m => m.ResolvedAddress is not null))
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
                new TargetConfig { Name = "Edge", Address = "203.0.113.1", Parent = "Router", Operator = "Comcast" },
                new TargetConfig { Name = "MS-1", Address = "20.0.0.1", Parent = "Edge", Operator = "Microsoft" },
                new TargetConfig { Name = "MS-2", Address = "20.0.0.2", Parent = "Edge", Operator = "microsoft " },
                new TargetConfig { Name = "MS-3", Address = "20.0.0.3", Parent = "Edge", Operator = "Microsoft" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Edge" },
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
        foreach (var m in monitors.Values.Where(m => m.ResolvedAddress is not null))
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

    /// <summary>Builds a coordinator stack (topology + verdict) for an ad-hoc topology and makes it healthy.</summary>
    /// <param name="unresolved">Targets whose address never resolves: they stay undetermined.</param>
    private (VerdictCoordinator Verdict, Dictionary<Guid, TargetMonitor> Monitors, TargetGraph Graph) Stack(
        WatcherOptions options, params string[] unresolved)
    {
        var graph = TargetGraph.FromOptions(options);
        var monitors = new Dictionary<Guid, TargetMonitor>();
        foreach (var target in graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            if (!unresolved.Contains(target.Name))
                monitor.StartTracking(IPAddress.Parse(target.Address));
            monitors[target.Id] = monitor;
        }
        new TopologyCoordinator(graph, monitors, NullLogger<TopologyCoordinator>.Instance).Start();
        var verdict = new VerdictCoordinator(graph, graph.GetPoolByName("Internet"), monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance);
        verdict.Start();
        foreach (var m in monitors.Values.Where(m => m.ResolvedAddress is not null))
            for (int i = 0; i < 5; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                m.RecordPingResult(new PingResult(m.ResolvedAddress!, true, 5, timestamp: _time.GetUtcNow()));
            }
        return (verdict, monitors, graph);
    }

    [Fact]
    public void An_isp_owned_anchor_never_proves_the_internet_is_up()
    {
        // The ISP is cut off from the rest of the internet: its own DNS still answers.
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Edge", Address = "203.0.113.1", Parent = "Router", Operator = "Comcast" },
                new TargetConfig { Name = "Comcast DNS", Address = "75.75.75.75", Parent = "Edge", Operator = " comcast" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Edge" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Comcast DNS", "Google"] }],
        });
        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);

        FeedDown(monitors, graph, "Google");
        Assert.Equal(VerdictState.LinkDown, verdict.CurrentVerdict);
        Assert.Equal("LinkDown: Comcast — nothing answers beyond Edge", verdict.CurrentDetail);
    }

    [Fact]
    public void With_the_isp_unknown_one_answering_anchor_is_not_enough()
    {
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        });

        FeedDown(monitors, graph, "Google");
        FeedDown(monitors, graph, "Cloudflare");
        Assert.Equal(VerdictState.LinkDegraded, verdict.CurrentVerdict);
        Assert.Contains("only 1 answering, 2 needed", verdict.CurrentDetail);
        Assert.Contains("nothing answers beyond Router", verdict.CurrentDetail);
    }

    [Fact]
    public void With_the_isp_unknown_two_answering_anchors_prove_it_up()
    {
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        });

        FeedDown(monitors, graph, "Google");
        Assert.Equal(VerdictState.SiteIncident, verdict.CurrentVerdict);
    }

    [Fact]
    public void Anchors_hung_directly_off_the_root_with_none_answering_say_so()
    {
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Google", Address = "8.8.8.8" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        });

        FeedDown(monitors, graph, "Google");
        Assert.Equal(VerdictState.LinkDegraded, verdict.CurrentVerdict); // ISP unknown: one of two can't prove it
        Assert.Equal("LinkDegraded: only 1 answering, 2 needed", verdict.CurrentDetail);

        FeedDown(monitors, graph, "Cloudflare");
        Assert.Equal(VerdictState.LinkDown, verdict.CurrentVerdict);
        Assert.Equal("LinkDown: no anchor is answering", verdict.CurrentDetail);
    }

    [Fact]
    public void An_anchor_stuck_undetermined_does_not_hide_link_down_when_the_isp_is_known()
    {
        // Final review I2 / spec §4.2: answering = 0 with at least one counted
        // anchor determined is LinkDown, not "waiting".
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Edge", Address = "203.0.113.1", Parent = "Router", Operator = "Comcast" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Edge" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Edge" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Edge" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        }, unresolved: "Quad9");
        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);

        FeedDown(monitors, graph, "Google");
        FeedDown(monitors, graph, "Cloudflare");
        Assert.Equal(VerdictState.LinkDown, verdict.CurrentVerdict);
        Assert.Equal("LinkDown: Comcast — nothing answers beyond Edge", verdict.CurrentDetail);
    }

    [Fact]
    public void An_anchor_stuck_undetermined_does_not_hide_link_down_when_the_isp_is_unknown()
    {
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        }, unresolved: "Quad9");

        FeedDown(monitors, graph, "Google");
        FeedDown(monitors, graph, "Cloudflare");
        Assert.Equal(VerdictState.LinkDown, verdict.CurrentVerdict);
        Assert.Contains("nothing answers beyond Router", verdict.CurrentDetail);
    }

    [Fact]
    public void A_bypassed_down_hop_is_never_blamed_for_weather()
    {
        // Final review M1: the Modem's probe fails but Quad9 answers through
        // it, so weather-level anchor loss is weather, not "Comcast — Modem".
        AllHealthy();
        Fail("Modem");
        Fail("Google", "Cloudflare");
        Assert.Equal(HostState.Down, _monitors[_graph.GetByName("Modem").Id].CurrentState);
        Assert.Equal(VerdictState.InternetWeather, Verdict);
        Assert.DoesNotContain("Modem", _verdict.CurrentDetail);
    }

    [Fact]
    public void A_tagged_anchor_nested_under_another_anchor_does_not_make_its_operator_the_isp()
    {
        // Final review T4 M1: an anchor is not a path hop. Were Google's tag
        // taken as the ISP, Google would turn on-net and the lone off-net
        // anchor failing would read as LinkDown while Google still answers.
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router", Operator = "Google" },
                new TargetConfig { Name = "Google DNS", Address = "8.8.4.4", Parent = "Google" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Google DNS", "Cloudflare"] }],
        });

        FeedDown(monitors, graph, "Cloudflare");
        FeedDown(monitors, graph, "Google DNS");
        Assert.Equal(VerdictState.LinkDegraded, verdict.CurrentVerdict); // ISP unknown: one of two needed
        Assert.Contains("only 1 answering, 2 needed", verdict.CurrentDetail);
        Assert.Null(verdict.CurrentOperator);
    }

    [Fact]
    public void An_anchor_without_a_path_never_outranks_one_with_a_path_for_blame()
    {
        // Final review T4 M2.
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Root Anchor", Address = "9.9.9.9" },
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Root Anchor", "Google"] }],
        });

        FeedDown(monitors, graph, "Root Anchor");
        FeedDown(monitors, graph, "Google");
        Assert.Equal(VerdictState.LinkDown, verdict.CurrentVerdict);
        Assert.Equal("LinkDown: nothing answers beyond Router", verdict.CurrentDetail);
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

    [Fact]
    public void Current_is_one_snapshot_whose_detail_matches_its_state()
    {
        Feed("Google", 5, true); Feed("Cloudflare", 5, true); Feed("Quad9", 5, true);
        Feed("Modem", 5, true); Feed("Router", 5, true);
        var healthy = _verdict.Current;
        Assert.Equal(VerdictState.Healthy, healthy.State);
        Assert.StartsWith("Healthy", healthy.Detail);

        Fail("Modem", "Google", "Cloudflare", "Quad9");
        var down = _verdict.Current;
        Assert.Equal(VerdictState.LinkDown, down.State);
        Assert.StartsWith("LinkDown", down.Detail);
        Assert.Equal("Comcast", down.Operator);
        // The earlier snapshot is untouched: readers holding it never see a torn pair.
        Assert.Equal(VerdictState.Healthy, healthy.State);
    }

    [Fact]
    public void Verdict_transitions_are_published_outside_the_verdict_lock()
    {
        var results = new List<bool>();
        using var subscription = _verdict.TransitionSource.Subscribe(_ =>
            results.Add(Task.Run(() => _verdict.Seed(VerdictState.LinkDown)).Wait(TimeSpan.FromSeconds(10))));

        AllHealthy();

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r));
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict); // Seed stayed a no-op
    }
}
