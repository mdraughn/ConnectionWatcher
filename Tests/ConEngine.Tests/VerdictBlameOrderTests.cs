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

/// <summary>During weather the state rule blames the shallowest losing path hop
/// (spec 2026-10-09 §4.5, veto removed 2026-10-12); a hop whose ProbeOnlyLoss note
/// holds is never blamed (2026-10-13).</summary>
public class VerdictBlameOrderTests : IDisposable
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _topology;
    // Read by the ModelCheckCoordinator the veto tests start; the verdict reads only its note.
    private readonly LossWindows _loss = new();
    private readonly VerdictCoordinator _verdict;
    private readonly List<TargetTransition> _transitions = new();
    private readonly List<ModelCheckCoordinator> _checks = new();
    // What the branching-topology tests build, disposed with the fixture.
    private readonly List<IDisposable> _branching = new();

    public VerdictBlameOrderTests()
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
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        _topology = new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance);
        _topology.Start();
        _verdict = new VerdictCoordinator(_graph, _graph.GetPoolByName("Internet"), _monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance);
        _verdict.TransitionSource.Subscribe(_transitions.Add);
        _verdict.Start();
    }

    public void Dispose()
    {
        _verdict.Dispose();
        foreach (var c in _checks)
            c.Dispose();
        foreach (var d in _branching)
            d.Dispose();
        _topology.Dispose();
        foreach (var m in _monitors.Values)
            m.Dispose();
    }

    private Guid Id(string name) => _graph.GetByName(name).Id;

    /// <summary>Drives a monitor's state (the state machine), not the windows.</summary>
    private void Feed(string name, int count, bool success)
    {
        var monitor = _monitors[Id(name)];
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    /// <summary>Records one window's samples for the given targets, then closes
    /// it with one sample past the close delay (time moves forward to it).</summary>
    private void CloseWindow(params (string Name, int Samples, int Lost)[] targets)
    {
        var start = LossWindows.WindowStart(_time.GetUtcNow());
        foreach (var (name, samples, lost) in targets)
            for (int i = 0; i < samples; i++)
                _loss.Record(Id(name), start + TimeSpan.FromSeconds(i * 50.0 / samples), success: i >= lost);
        _time.SetUtcNow(start + LossWindows.Span + LossWindows.CloseDelay);
        _loss.Record(Id("Router"), _time.GetUtcNow(), true);
    }

    /// <summary>The modem drops its own pings; Quad9 beyond it loses nothing.</summary>
    private void CloseProbeOnlyWindow() =>
        CloseWindow(("Router", 12, 0), ("Modem", 12, 5), ("Google", 12, 12), ("Cloudflare", 12, 6), ("Quad9", 12, 0));

    /// <summary>The modem and everything beyond it lose about as much.</summary>
    private void CloseRealLossWindow() =>
        CloseWindow(("Router", 12, 0), ("Modem", 12, 4), ("Google", 12, 5), ("Cloudflare", 12, 4), ("Quad9", 12, 4));

    private void Healthy()
    {
        foreach (var name in new[] { "Router", "Modem", "Google", "Cloudflare", "Quad9" })
            Feed(name, 5, true);
    }

    private void Weather()
    {
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false); // two anchors down: weather level (threshold 2)
    }

    private void WeatherWithAFalteringModem()
    {
        Healthy();
        Weather();
        Feed("Modem", 3, false); // and the modem's own probe falters
    }


    [Fact]
    public void A_faltering_modem_during_weather_is_blamed_with_its_operator()
    {
        WeatherWithAFalteringModem();
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Comcast — Modem is Faltering", _verdict.CurrentDetail);
    }

    [Fact]
    public void Loss_starting_at_the_router_blames_the_router_not_the_modem()
    {
        Healthy();
        Weather();
        Feed("Router", 3, false);
        Feed("Modem", 3, false);
        Assert.Equal(HostState.Faltering, _monitors[Id("Router")].CurrentState);
        Assert.Equal(HostState.Faltering, _monitors[Id("Modem")].CurrentState);

        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Router is Faltering", _verdict.CurrentDetail); // the shallowest
    }

    private ModelCheckCoordinator StartVeto()
    {
        var check = new ModelCheckCoordinator(_graph, _monitors, unprovableUp: false, _time,
            NullLogger<ModelCheckCoordinator>.Instance, _loss);
        _checks.Add(check);
        _verdict.UseProbeOnlyVeto(check);
        check.Start();
        return check;
    }

    /// <summary>Lets the check's timer fire, so it reads the windows closed so far.</summary>
    private void Evaluate() => _time.Advance(ModelCheckCoordinator.EvaluateEvery);

    private static bool NoteHolds(ModelCheckCoordinator check) =>
        check.Active.Any(c => c.Kind == ModelWarningKind.ProbeOnlyLoss);

    private Dictionary<Guid, HostState> States() =>
        _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);

    [Fact]
    public void A_hop_whose_probe_only_note_holds_is_not_blamed_for_weather()
    {
        var check = StartVeto();
        WeatherWithAFalteringModem();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseProbeOnlyWindow();
        Evaluate();

        Assert.True(check.Holds(ModelWarningKind.ProbeOnlyLoss, Id("Modem")));
        Assert.Equal(HostState.Faltering, _monitors[Id("Modem")].CurrentState);
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
    }

    [Fact]
    public void Before_the_note_holds_the_faltering_hop_is_still_blamed()
    {
        var check = StartVeto();
        WeatherWithAFalteringModem();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows - 1; i++)
            CloseProbeOnlyWindow();
        Evaluate();

        // The known gap: the note needs 10 windows (about 10 minutes) to prove itself.
        Assert.False(check.Holds(ModelWarningKind.ProbeOnlyLoss, Id("Modem")));
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Comcast — Modem is Faltering", _verdict.CurrentDetail);
    }

    [Fact]
    public void The_note_raising_reclassifies_without_any_state_change()
    {
        StartVeto();
        WeatherWithAFalteringModem();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows - 1; i++)
            CloseProbeOnlyWindow();
        Evaluate();
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        var before = States();

        CloseProbeOnlyWindow(); // the 10th
        Evaluate();

        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
        Assert.Equal(before, States());
    }

    [Fact]
    public void Real_loss_at_the_hop_raises_no_note_so_it_is_still_blamed()
    {
        var check = StartVeto();
        WeatherWithAFalteringModem();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseRealLossWindow();
        Evaluate();

        Assert.False(NoteHolds(check));
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Comcast — Modem is Faltering", _verdict.CurrentDetail);
    }

    [Fact]
    public void When_the_note_clears_the_hop_can_be_blamed_again()
    {
        var check = StartVeto();
        WeatherWithAFalteringModem();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseProbeOnlyWindow();
        Evaluate();
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);

        // Now the loss is real: what lies beyond the modem loses as much as it does.
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseRealLossWindow();
        Evaluate();

        Assert.False(NoteHolds(check));
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
    }
    [Fact]
    public void Wiring_the_veto_does_not_unseat_a_seeded_verdict_while_anchors_are_undetermined()
    {
        using var verdict = new VerdictCoordinator(_graph, _graph.GetPoolByName("Internet"), _monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance);
        var transitions = new List<TargetTransition>();
        verdict.TransitionSource.Subscribe(transitions.Add);
        verdict.Seed(VerdictState.Healthy);
        verdict.Start();
        using var check = new ModelCheckCoordinator(_graph, _monitors, unprovableUp: false, _time,
            NullLogger<ModelCheckCoordinator>.Instance, _loss);

        verdict.UseProbeOnlyVeto(check);

        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        Assert.Empty(transitions);
    }

    /// <summary>Two WANs (EV5): Router → ModemA (Comcast) → {Google, Cloudflare};
    /// Router → ModemB (Verizon) → Inner → Quad9. Paths, root first: Google and
    /// Cloudflare [Router, ModemA]; Quad9 [Router, ModemB, Inner].</summary>
    private (VerdictCoordinator Verdict, Dictionary<Guid, TargetMonitor> Monitors, TargetGraph Graph) Branching(
        string[] pool, params TargetConfig[] extra)
    {
        var graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.0.2.1", Profile = "Internal" },
                new TargetConfig { Name = "ModemA", Address = "192.0.2.2", Parent = "Router", Operator = "Comcast" },
                new TargetConfig { Name = "ModemB", Address = "192.0.2.3", Parent = "Router", Operator = "Verizon" },
                new TargetConfig { Name = "Inner", Address = "203.0.113.1", Parent = "ModemB" },
                new TargetConfig { Name = "Google", Address = "198.51.100.8", Parent = "ModemA" },
                new TargetConfig { Name = "Cloudflare", Address = "198.51.100.1", Parent = "ModemA" },
                new TargetConfig { Name = "Quad9", Address = "198.51.100.9", Parent = "Inner" },
                .. extra,
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = [.. pool] }],
        });
        var monitors = new Dictionary<Guid, TargetMonitor>();
        foreach (var target in graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitors[target.Id] = monitor;
            _branching.Add(monitor);
        }
        var topology = new TopologyCoordinator(graph, monitors, NullLogger<TopologyCoordinator>.Instance);
        topology.Start();
        _branching.Add(topology);
        var verdict = new VerdictCoordinator(graph, graph.GetPoolByName("Internet"), monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance);
        verdict.Start();
        _branching.Add(verdict);
        foreach (var m in monitors.Values)
            for (int i = 0; i < 5; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                m.RecordPingResult(new PingResult(m.ResolvedAddress!, true, 5, timestamp: _time.GetUtcNow()));
            }
        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        return (verdict, monitors, graph);
    }

    private void FeedTo(Dictionary<Guid, TargetMonitor> monitors, TargetGraph graph, string name, int count, bool success)
    {
        var monitor = monitors[graph.GetByName(name).Id];
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    private VerdictCoordinator.VerdictSnapshot EveryAnchorDownBehindAnsweringHops(string[] pool)
    {
        var (verdict, monitors, graph) = Branching(pool);
        foreach (var anchor in pool)
            FeedTo(monitors, graph, anchor, 20, false);
        foreach (var hop in new[] { "Router", "ModemA", "ModemB", "Inner" })
            Assert.Equal(HostState.Responding, monitors[graph.GetByName(hop).Id].CurrentState);
        return verdict.Current;
    }

    /// <summary>EV5 (EV-R19): every hop answers, so each failing anchor's frontier is its
    /// last hop. Google and Cloudflare blame path index 2 (beyond ModemA); Quad9 blames
    /// index 3 (beyond Inner). The smallest index wins whatever the pool order.</summary>
    [Fact]
    public void Blame_takes_the_shallowest_frontier_across_branching_paths()
    {
        var forward = EveryAnchorDownBehindAnsweringHops(["Google", "Cloudflare", "Quad9"]);
        var reversed = EveryAnchorDownBehindAnsweringHops(["Quad9", "Cloudflare", "Google"]);

        Assert.Equal(VerdictState.LinkDown, forward.State);
        Assert.Contains("nothing answers beyond ModemA", forward.Detail);
        Assert.Equal("Comcast", forward.Operator);
        Assert.Contains("nothing answers beyond ModemA", reversed.Detail);
        Assert.Equal(forward, reversed);
    }

    /// <summary>EV5 (EV-R30): during weather, a Down hop with nothing answering beyond it
    /// is a losing hop, and the shallowest losing hop is blamed. ModemB (path index 1) is
    /// Down with Inner and Quad9 silent behind it; the only Faltering hop, Agg, is at
    /// index 2 on the other branch. Level3 behind Agg still answers.</summary>
    [Fact]
    public void A_down_hop_with_nothing_beyond_is_blamed_for_weather()
    {
        var (verdict, monitors, graph) = Branching(["Google", "Cloudflare", "Quad9", "Level3"],
            new TargetConfig { Name = "Agg", Address = "192.0.2.20", Parent = "ModemA" },
            new TargetConfig { Name = "Level3", Address = "198.51.100.4", Parent = "Agg" });
        HostState State(string name) => monitors[graph.GetByName(name).Id].CurrentState;

        FeedTo(monitors, graph, "Google", 20, false);
        FeedTo(monitors, graph, "Cloudflare", 20, false); // weather level (threshold 2)
        FeedTo(monitors, graph, "ModemB", 20, false);
        FeedTo(monitors, graph, "Inner", 20, false);
        FeedTo(monitors, graph, "Quad9", 20, false);      // nothing answers beyond ModemB
        FeedTo(monitors, graph, "Agg", 3, false);
        Assert.Equal(HostState.Down, State("ModemB"));
        Assert.Equal(HostState.Faltering, State("Agg"));
        Assert.Equal(HostState.Responding, State("Level3"));

        Assert.Equal(VerdictState.LinkDegraded, verdict.CurrentVerdict);
        Assert.Contains("ModemB is Down", verdict.CurrentDetail);
        Assert.Equal("Verizon", verdict.CurrentOperator);
    }
}
