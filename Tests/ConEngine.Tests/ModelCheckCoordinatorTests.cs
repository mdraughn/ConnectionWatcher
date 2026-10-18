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

public class ModelCheckCoordinatorTests : IDisposable
{
    private static readonly PingProfile Fast = new()
    {
        Name = "Fast", HistorySize = 20, MinUsableSample = 5,
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-09T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _topology;
    private ModelCheckCoordinator _check = null!;
    private readonly List<ModelConditionChange> _changes = new();

    public ModelCheckCoordinatorTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Inside", Address = "10.1.10.1", Parent = "Router", Profile = "Internal", Probe = "tcp:80" },
                new TargetConfig { Name = "External", Address = "203.0.113.1", Parent = "Inside", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "External" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "External" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
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
    }

    private readonly LossWindows _loss = new();

    private void StartCheck(bool unprovableUp = false)
    {
        _check = new ModelCheckCoordinator(_graph, _monitors, unprovableUp, _time,
            NullLogger<ModelCheckCoordinator>.Instance, _loss);
        _check.ChangeSource.Subscribe(_changes.Add);
        _check.Start();
    }

    /// <summary>One closed window: Inside loses <paramref name="insideLost"/> of 12,
    /// External loses <paramref name="beyondLost"/> of 12, the rest nothing.</summary>
    private void CloseWindow(int insideLost, int beyondLost = 0)
    {
        var start = LossWindows.WindowStart(_time.GetUtcNow());
        void Fill(string name, int lost)
        {
            for (int i = 0; i < 12; i++)
                _loss.Record(Id(name), start + TimeSpan.FromSeconds(i * 4), success: i >= lost);
        }
        Fill("Router", 0);
        Fill("Inside", insideLost);
        Fill("External", beyondLost);
        Fill("Google", beyondLost);
        Fill("Cloudflare", beyondLost);
        _time.SetUtcNow(start + LossWindows.Span + LossWindows.CloseDelay);
        _loss.Record(Id("Router"), _time.GetUtcNow(), true);
        _time.Advance(ModelCheckCoordinator.EvaluateEvery); // let the periodic evaluation run
    }

    public void Dispose()
    {
        _check?.Dispose();
        _topology.Dispose();
        foreach (var m in _monitors.Values)
            m.Dispose();
    }

    private Guid Id(string name) => _graph.GetByName(name).Id;

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

    private void AllHealthy()
    {
        foreach (var name in new[] { "Router", "Inside", "External", "Google", "Cloudflare" })
            Feed(name, 5, true);
    }

    [Fact]
    public void The_2026_10_08_incident_raises_one_hop_bypassed_note_and_clears_it()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 20, false); // Inside Down; External and the anchors keep answering

        var raised = Assert.Single(_changes);
        Assert.True(raised.Active);
        Assert.Equal(ModelWarningKind.HopBypassed, raised.Condition.Kind);
        Assert.Equal($"hop-bypassed:{Id("Inside"):D}", raised.Condition.Key);
        Assert.Equal(Id("Inside"), raised.Condition.TargetId);
        Assert.Equal("Inside", raised.Condition.TargetName);
        Assert.StartsWith("Inside isn't answering its probe (tcp/80), but External beyond it is",
            raised.Condition.Message);

        Feed("Inside", 20, true);
        Assert.Equal(2, _changes.Count);
        Assert.False(_changes[1].Active);
        Assert.Equal(raised.Condition.Key, _changes[1].Condition.Key);
        Assert.Empty(_check.Active);
    }

    [Fact]
    public void A_real_outage_beyond_a_hop_raises_no_note()
    {
        StartCheck();
        AllHealthy();
        // Everything beyond fails first, then the hop: nothing beyond ever answers.
        Feed("External", 20, false);
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);
        Feed("Inside", 20, false);

        Assert.DoesNotContain(_changes, c => c.Active && c.Condition.TargetId == Id("Inside"));
        Assert.DoesNotContain(_check.Active, c => c.TargetId == Id("Inside"));
    }

    [Fact]
    public void A_hop_whose_descendants_go_down_after_it_stops_being_bypassed()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 20, false);
        Assert.Single(_check.Active);

        Feed("External", 20, false);
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);

        Assert.Empty(_check.Active);
        Assert.False(_changes.Last().Active);
    }

    [Fact]
    public void A_faltering_hop_is_a_note_only_after_it_holds_for_the_faltering_hold()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 3, false);
        Assert.Equal(HostState.Faltering, _monitors[Id("Inside")].CurrentState);
        Assert.Empty(_changes);

        // Ticks are aligned to Start, not to the Faltering transition: one
        // extra period guarantees a tick after the hold has passed.
        _time.Advance(ModelCheckCoordinator.FalteringHold + ModelCheckCoordinator.EvaluateEvery);
        var raised = Assert.Single(_changes);
        Assert.Equal(ModelWarningKind.HopBypassed, raised.Condition.Kind);
        Assert.StartsWith("Inside is losing replies to its probe (tcp/80), but External beyond it answers",
            raised.Condition.Message);
    }

    [Fact]
    public void A_faltering_blip_shorter_than_the_hold_leaves_no_note()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 3, false);
        Feed("Inside", 20, true); // recovered well inside the hold
        _time.Advance(ModelCheckCoordinator.FalteringHold * 2);
        Assert.Empty(_changes);
    }

    [Fact]
    public void A_hop_that_never_answers_becomes_never_answered_after_the_wait()
    {
        StartCheck();
        foreach (var name in new[] { "Router", "External", "Google", "Cloudflare" })
            Feed(name, 5, true);
        Feed("Inside", 20, false); // never a single reply
        Assert.Empty(_check.Active); // no note at all until the wait has passed

        // Keep the beyond-targets answering while the clock passes the wait.
        while (_time.GetUtcNow() - DateTimeOffset.Parse("2026-10-09T00:00:00Z") < ModelCheckCoordinator.NeverAnsweredAfter)
            Feed("External", 5, true);
        _time.Advance(ModelCheckCoordinator.EvaluateEvery);

        var active = Assert.Single(_check.Active);
        Assert.Equal(ModelWarningKind.NeverAnswered, active.Kind);
        Assert.Equal($"never-answered:{Id("Inside"):D}", active.Key);
        Assert.StartsWith("Inside has never answered its probe (tcp/80)", active.Message);
        Assert.DoesNotContain(_changes, c => c.Condition.Kind == ModelWarningKind.HopBypassed);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_break_the_ping_result_path()
    {
        StartCheck();
        foreach (var name in new[] { "Router", "External", "Google", "Cloudflare" })
            Feed(name, 5, true);
        Feed("Inside", 5, true);
        _check.ChangeSource.Subscribe(_ => throw new InvalidOperationException("boom"));

        var ex = Record.Exception(() => Feed("Inside", 20, false));

        Assert.Null(ex);
    }

    [Fact]
    public void A_hop_that_answered_once_is_never_never_answered()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 20, false);
        _time.Advance(ModelCheckCoordinator.NeverAnsweredAfter * 2);
        Assert.All(_check.Active, c => Assert.Equal(ModelWarningKind.HopBypassed, c.Kind));
    }

    [Fact]
    public void Unprovable_up_is_raised_at_start_when_every_anchor_is_the_isps()
    {
        StartCheck(unprovableUp: true);
        var raised = Assert.Single(_changes);
        Assert.True(raised.Active);
        Assert.Equal(ModelWarningKind.UnprovableUp, raised.Condition.Kind);
        Assert.Equal("unprovable-up", raised.Condition.Key);
        Assert.Null(raised.Condition.TargetId);
        Assert.Contains("Tag your ISP on a path hop (or add an anchor outside your ISP) so ConnectionWatcher can prove your internet is up.",
            raised.Condition.Message);
    }

    [Fact]
    public void Repeated_evaluations_of_a_standing_condition_publish_nothing_new()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 20, false);
        Feed("External", 10, true);
        _time.Advance(ModelCheckCoordinator.EvaluateEvery * 10);
        Assert.Single(_changes);
    }

    [Fact]
    public void Changes_are_published_outside_the_coordinator_lock()
    {
        // A subscriber reading Active from another thread inside OnNext would
        // deadlock if the change were published under the lock.
        StartCheck();
        var reads = new List<int>();
        _check.ChangeSource.Subscribe(_ =>
            reads.Add(Task.Run(() => _check.Active.Count).Wait(TimeSpan.FromSeconds(10)) ? 1 : 0));
        AllHealthy();
        Feed("Inside", 20, false);
        Assert.Equal([1], reads);
    }

    [Fact]
    public void Dispose_stops_the_periodic_evaluation()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 3, false);
        _check.Dispose();
        _time.Advance(ModelCheckCoordinator.FalteringHold * 2); // must not throw or publish
        Assert.Empty(_changes);
    }

    [Fact]
    public void A_hop_dropping_only_its_own_pings_is_a_note_after_ten_windows_and_not_before()
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows - 1; i++)
            CloseWindow(insideLost: 3); // 25 % of its own probes, 0 % beyond
        Assert.DoesNotContain(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);

        CloseWindow(insideLost: 3);
        var note = Assert.Single(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
        Assert.Equal($"probe-only-loss:{Id("Inside"):D}", note.Key);
        Assert.Equal("Inside drops some of its own pings but forwards fine (loss 25 % · forwarding 0 % " +
            "over the last 10 minutes) — common router behaviour; consider a different probe.", note.Message);
    }

    [Fact]
    public void Real_forwarding_loss_is_never_probe_only()
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 3, beyondLost: 3); // everything beyond loses as much
        Assert.DoesNotContain(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
    }

    [Fact]
    public void The_note_clears_with_hysteresis_not_on_one_clean_window()
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 3);
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);

        CloseWindow(insideLost: 0); // average now 22.5 %: still above the 10 % clear line
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);

        for (int i = 0; i < 6; i++)
            CloseWindow(insideLost: 0); // average now 7.5 %
        Assert.DoesNotContain(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
    }

    [Fact]
    public void Probe_only_loss_never_changes_any_targets_state()
    {
        StartCheck();
        AllHealthy();
        var before = _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 3);
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
        Assert.Equal(before, _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState));
    }

    [Fact]
    public void A_faltering_hop_with_a_probe_only_note_is_not_also_bypassed()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 3, false);
        Assert.Equal(HostState.Faltering, _monitors[Id("Inside")].CurrentState);

        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 3); // ten minutes: well past the faltering hold
        Assert.Equal(HostState.Faltering, _monitors[Id("Inside")].CurrentState);

        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss && c.TargetId == Id("Inside"));
        Assert.DoesNotContain(_check.Active, c => c.Kind == ModelWarningKind.HopBypassed && c.TargetId == Id("Inside"));
    }

    [Fact]
    public void An_icmp_hop_is_advised_to_try_tcp()
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
        {
            // Router (ICMP) is the hop this time: it loses 3 of 12, everything beyond nothing.
            var start = LossWindows.WindowStart(_time.GetUtcNow());
            foreach (var name in new[] { "Router", "Inside", "External", "Google", "Cloudflare" })
                for (int s = 0; s < 12; s++)
                    _loss.Record(Id(name), start + TimeSpan.FromSeconds(s * 4), success: name != "Router" || s >= 3);
            _time.SetUtcNow(start + LossWindows.Span + LossWindows.CloseDelay);
            _loss.Record(Id("Inside"), _time.GetUtcNow(), true);
            _time.Advance(ModelCheckCoordinator.EvaluateEvery);
        }
        var note = Assert.Single(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
        Assert.EndsWith("consider a TCP probe.", note.Message);
    }

    [Fact]
    public void Real_forwarding_loss_blocks_the_note()
    {
        // Inside loses half its probes but everything beyond loses a little too:
        // some of that loss is real forwarding loss, so it is not probe-only.
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 6, beyondLost: 1);
        Assert.DoesNotContain(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
    }

    [Theory]
    [InlineData(2, true)]   // about 17 % of its own probes: raised
    [InlineData(1, false)]  // about 8 %: below the raise line
    public void Probe_only_raise_boundary(int insideLost, bool raised)
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: insideLost);
        Assert.Equal(raised, _check.Active.Any(c => c.Kind == ModelWarningKind.ProbeOnlyLoss));
    }

    [Fact]
    public void Forwarding_loss_clears_a_held_note()
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 3);
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);

        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 6, beyondLost: 1); // own loss stays far above the clear line
        Assert.DoesNotContain(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
    }

    [Fact]
    public void A_held_note_survives_a_little_forwarding_loss()
    {
        // Hysteresis: forwarding loss may grow past the raise limit and the held note stays.
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 3);
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);

        for (int i = 0; i < 5; i++)
            CloseWindow(insideLost: 3, beyondLost: 1); // average forwarding loss about 4 %
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
    }

    [Fact]
    public void A_refusing_tcp_hop_is_answered_not_never_answered()
    {
        StartCheck();
        foreach (var name in new[] { "Router", "External", "Google", "Cloudflare" })
            Feed(name, 5, true);
        // Inside (tcp/80) answered only with refusals, then went quiet.
        var inside = _monitors[Id("Inside")];
        for (int i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            inside.RecordPingResult(new PingResult(inside.ResolvedAddress!, true, 5, "Refused",
                timestamp: _time.GetUtcNow()));
        }
        Assert.Equal(0, inside.AcceptedReplies);
        Feed("Inside", 20, false);

        while (_time.GetUtcNow() - DateTimeOffset.Parse("2026-10-09T00:00:00Z") < ModelCheckCoordinator.NeverAnsweredAfter)
            Feed("External", 5, true);
        _time.Advance(ModelCheckCoordinator.EvaluateEvery);

        var active = Assert.Single(_check.Active);
        Assert.Equal(ModelWarningKind.HopBypassed, active.Kind);
        Assert.Equal(Id("Inside"), active.TargetId);
        Assert.DoesNotContain(_changes, c => c.Condition.Kind == ModelWarningKind.NeverAnswered);
    }

    [Fact]
    public void Model_checks_apply_to_a_lan_switch()
    {
        var graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "LanRouter", Address = "192.0.2.1", Profile = "Internal", Scope = TargetScope.Local },
                new TargetConfig { Name = "LanSwitch", Address = "192.0.2.2", Parent = "LanRouter", Profile = "Internal", Scope = TargetScope.Local },
                new TargetConfig { Name = "LanCamera", Address = "192.0.2.3", Parent = "LanSwitch", Profile = "Internal", Scope = TargetScope.Local },
            ],
        });
        var monitors = new Dictionary<Guid, TargetMonitor>();
        foreach (var target in graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitors[target.Id] = monitor;
        }
        void LanFeed(string name, int count, bool success)
        {
            var monitor = monitors[graph.GetByName(name).Id];
            for (int i = 0; i < count; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                    success ? 5 : 0, timestamp: _time.GetUtcNow()));
            }
        }
        using var check = new ModelCheckCoordinator(graph, monitors, false, _time,
            NullLogger<ModelCheckCoordinator>.Instance);
        check.Start();
        try
        {
            foreach (var name in new[] { "LanRouter", "LanSwitch", "LanCamera" })
                LanFeed(name, 5, true);
            LanFeed("LanSwitch", 20, false); // the switch's probe fails; the camera behind it answers

            var note = Assert.Single(check.Active);
            Assert.Equal(ModelWarningKind.HopBypassed, note.Kind);
            Assert.Equal("LanSwitch", note.TargetName);
        }
        finally
        {
            foreach (var m in monitors.Values)
                m.Dispose();
        }
    }
}
