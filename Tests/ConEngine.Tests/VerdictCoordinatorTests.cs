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
                new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
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
