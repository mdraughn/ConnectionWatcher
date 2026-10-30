using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

/// <summary>
/// Warm restart semantics: seeded (presumed) states show immediately, emit no
/// transition when fresh samples confirm them, and correct honestly when they
/// don't. Spec: docs/superpowers/specs/2026-09-11-warm-restart-design.md.
/// </summary>
public class WarmStartTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("10.0.0.1");
    private static readonly PingProfile Profile = new()
    {
        Name = "Test",
        HistorySize = 20,
        MinUsableSample = 5,
        RespondingThreshold = 0.95,
        FalteringThreshold = 0.75,
        DownThreshold = 0.10,
        RecoveringThreshold = 0.25,
    };

    private static (TrackerProbe tracker, List<AnalysisTransition> transitions, FakeTimeProvider time) Make()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        var tracker = new TrackerProbe(Ip, Profile, time);
        return (tracker, tracker.Transitions, time);
    }

    private static void Feed(TrackerProbe tracker, FakeTimeProvider time, int count, bool success)
    {
        for (int i = 0; i < count; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            tracker.RecordPingResult(new PingResult(Ip, success, success ? 10 : 0,
                timestamp: time.GetUtcNow()));
        }
    }

    [Fact]
    public void Confirmed_seed_emits_no_transition()
    {
        var (tracker, transitions, time) = Make();
        tracker.Seed(HostState.Responding);
        Assert.Equal(HostState.Responding, tracker.CurrentState);

        Feed(tracker, time, 10, success: true);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
        Assert.Empty(transitions); // the timeline is continuous across the restart
    }

    [Fact]
    public void Seed_is_assumed_until_min_sample_confirms_it()
    {
        var (tracker, _, time) = Make();
        tracker.Seed(HostState.Responding);
        Assert.True(tracker.Assumed);
        Feed(tracker, time, Profile.MinUsableSample - 1, success: true);
        Assert.True(tracker.Assumed);
        Feed(tracker, time, 1, success: true);
        Assert.False(tracker.Assumed);
    }

    [Fact]
    public void Early_loss_does_not_flip_a_seeded_state()
    {
        var (tracker, transitions, time) = Make();
        tracker.Seed(HostState.Responding);

        // A cold Responding host would flip to Faltering on one lost ping
        // (success rate 0 over sample size 1); a presumption under test must
        // wait for a usable sample.
        Feed(tracker, time, 1, success: false);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
        Assert.Empty(transitions);
    }

    [Fact]
    public void Contradicted_seed_corrects_once_from_the_seeded_state()
    {
        var (tracker, transitions, time) = Make();
        tracker.Seed(HostState.Responding);
        Feed(tracker, time, 5, success: false);

        Assert.Equal(HostState.Down, tracker.CurrentState);
        var t = Assert.Single(transitions);
        Assert.Equal(HostState.Responding, t.From); // honest: from the presumption
        Assert.Equal(HostState.Down, t.To);
        Assert.Equal(TransitionTrigger.ThresholdCrossed, t.Reason.Trigger);
    }

    [Fact]
    public void Seeded_down_that_proves_alive_exits_via_recovering()
    {
        var (tracker, transitions, time) = Make();
        tracker.Seed(HostState.Down);
        Feed(tracker, time, 6, success: true);

        // A recovery is a recovery: it surfaces as Down -> Recovering, which
        // bursts everything beyond this hop.
        Assert.Equal(HostState.Down, transitions[0].From);
        Assert.Equal(HostState.Recovering, transitions[0].To);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
    }

    [Fact]
    public void Seeded_unreachable_confirmed_by_topology_stays_silent()
    {
        var (tracker, transitions, time) = Make();
        tracker.Seed(HostState.Unreachable);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);

        tracker.SetExplanation(Guid.NewGuid()); // topology names the root cause
        Feed(tracker, time, 5, success: false);  // fresh samples confirm: still down
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
        Assert.Empty(transitions);
    }

    [Fact]
    public void Seeded_unreachable_without_a_root_cause_is_released_to_starting_not_down()
    {
        // Final review I1: with no fresh samples there is no evidence of an
        // own Down, so a withdrawn placeholder cause re-proves from Starting.
        var (tracker, transitions, _) = Make();
        tracker.Seed(HostState.Unreachable);
        Assert.Null(tracker.ExplainedBy); // the placeholder is not a cause

        tracker.SetExplanation(null); // topology: nothing upstream explains it
        Assert.Equal(HostState.Starting, tracker.CurrentState);
        Assert.False(tracker.Assumed);
        var t = transitions.Single();
        Assert.Equal((HostState.Unreachable, HostState.Starting), (t.From, t.To));
        Assert.Equal(TransitionTrigger.ParentRecovered, t.Reason.Trigger);
        Assert.Null(t.Reason.AncestorId); // no cause was ever named
    }

    [Fact]
    public void Seeded_unreachable_that_proves_alive_exits_via_recovering()
    {
        var (tracker, transitions, time) = Make();
        tracker.Seed(HostState.Unreachable);
        tracker.SetExplanation(Guid.NewGuid());

        Feed(tracker, time, 6, success: true);
        Assert.Equal((HostState.Unreachable, HostState.Recovering), (transitions[0].From, transitions[0].To));
        Assert.Equal(HostState.Responding, tracker.CurrentState);
    }

    [Fact]
    public void Monitor_seed_applies_through_start_tracking()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        var target = new Target
        {
            Id = Guid.NewGuid(), Name = "Router", Address = "10.0.0.1", ProfileName = "Test",
        };
        var monitor = new TargetMonitor(target, Profile, new PingDiscipline(Profile, time),
            time, NullLoggerFactory.Instance);
        var transitions = new List<TargetTransition>();
        monitor.TransitionSource.Subscribe(transitions.Add);

        monitor.Seed(HostState.Responding);
        Assert.Equal(HostState.Responding, monitor.CurrentState); // pre-tracker
        Assert.True(monitor.IsAssumed);

        monitor.StartTracking(Ip);
        Assert.Equal(HostState.Responding, monitor.CurrentState);
        for (int i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(Ip, true, 10, timestamp: time.GetUtcNow()));
        }
        Assert.False(monitor.IsAssumed);
        Assert.Empty(transitions);
    }

    [Fact]
    public async Task Probe_change_discards_the_seed()
    {
        var factory = new FakePingTransceiverFactory();
        var ip = IPAddress.Parse("192.168.1.100");
        var targetId = Guid.NewGuid();
        var options = new WatcherOptions
        {
            Profiles =
            {
                ["Test"] = new PingProfileConfig
                {
                    NormalIntervalSeconds = 0.02,
                    BurstIntervalSeconds = 0.01,
                    BurstMaxDurationSeconds = 5,
                    HistorySize = 20,
                    MinUsableSample = 5,
                },
            },
            Targets =
            [
                new TargetConfig { Id = targetId, Name = "Private", Address = "192.168.1.100", Profile = "Test", Probe = "tcp:80" }
            ],
        };
        var seed = new EngineSeed
        {
            Targets = new Dictionary<Guid, TargetSeed>
            {
                [targetId] = new TargetSeed(HostState.Down, "192.168.1.100", Probe: null)  // Same addr, different probe
            }
        };
        var engine = new WatcherEngine(TimeProvider.System, factory,
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Options.Create(options));
        var cts = new CancellationTokenSource();
        try
        {
            await engine.StartAsync(cts.Token, seed);
            var monitor = engine.Monitors[targetId];
            // Seed not adopted (probe mismatch) => monitor not assumed, starts from Starting
            Assert.False(monitor.IsAssumed);
            Assert.NotEqual(HostState.Down, monitor.CurrentState);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public void Pool_seed_is_silent_then_corrected_by_member_evidence()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        var a = new Target { Id = Guid.NewGuid(), Name = "A", Address = "10.0.0.1", ProfileName = "Test" };
        var b = new Target { Id = Guid.NewGuid(), Name = "B", Address = "10.0.0.2", ProfileName = "Test" };
        var monitors = new[] { a, b }.Select(t =>
        {
            var m = new TargetMonitor(t, Profile, new PingDiscipline(Profile, time),
                time, NullLoggerFactory.Instance);
            m.Seed(HostState.Responding);
            m.StartTracking(IPAddress.Parse(t.Address));
            return m;
        }).ToList();
        var pool = new PoolCoordinator(new PoolDefinition
        {
            Id = Guid.NewGuid(), Name = "P", MemberTargetIds = [a.Id, b.Id],
        }, monitors, time, NullLogger<PoolCoordinator>.Instance);
        var transitions = new List<TargetTransition>();
        pool.TransitionSource.Subscribe(transitions.Add);
        pool.Seed(HostState.Responding);
        pool.Start();

        Assert.Equal(HostState.Responding, pool.CurrentState);
        Assert.Empty(transitions);

        // One member contradicts its seed: the pool recalculates as usual.
        for (int i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            monitors[0].RecordPingResult(new PingResult(
                IPAddress.Parse(a.Address), false, 0, timestamp: time.GetUtcNow()));
        }
        Assert.Equal(HostState.Responding, pool.CurrentState); // B still responding
        for (int i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            monitors[1].RecordPingResult(new PingResult(
                IPAddress.Parse(b.Address), false, 0, timestamp: time.GetUtcNow()));
        }
        Assert.Equal(HostState.Down, pool.CurrentState);
        Assert.Contains(transitions, t => t.From == HostState.Responding);
    }

    private static (PoolCoordinator pool, List<TargetTransition> transitions) MakePool(HostState? memberSeed)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        var a = new Target { Id = Guid.NewGuid(), Name = "A", Address = "10.0.0.1", ProfileName = "Test" };
        var b = new Target { Id = Guid.NewGuid(), Name = "B", Address = "10.0.0.2", ProfileName = "Test" };
        var monitors = new[] { a, b }.Select(t =>
        {
            var m = new TargetMonitor(t, Profile, new PingDiscipline(Profile, time),
                time, NullLoggerFactory.Instance);
            if (memberSeed is { } s)
                m.Seed(s);
            m.StartTracking(IPAddress.Parse(t.Address));
            return m;
        }).ToList();
        var pool = new PoolCoordinator(new PoolDefinition
        {
            Id = Guid.NewGuid(), Name = "P", MemberTargetIds = [a.Id, b.Id],
        }, monitors, time, NullLogger<PoolCoordinator>.Instance);
        var transitions = new List<TargetTransition>();
        pool.TransitionSource.Subscribe(transitions.Add);
        return (pool, transitions);
    }

    [Fact]
    public void A_new_pool_over_presumed_members_takes_their_state_at_start()
    {
        // The pool did not exist before this rebuild (no pool seed) and its members
        // were carried over: confirming them emits nothing, so start must decide.
        var (pool, transitions) = MakePool(HostState.Responding);
        pool.Start();

        Assert.Equal(HostState.Responding, pool.CurrentState);
        var t = Assert.Single(transitions);
        Assert.Equal(HostState.Initializing, t.From);
        Assert.Equal(HostState.Responding, t.To);
    }

    [Fact]
    public void A_cold_pool_start_stays_silent()
    {
        var (pool, transitions) = MakePool(memberSeed: null);
        pool.Start();

        Assert.Equal(HostState.Initializing, pool.CurrentState);
        Assert.Empty(transitions);
    }

    [Fact]
    public void A_seeded_pool_contradicted_by_its_presumed_members_corrects_at_start()
    {
        var (pool, transitions) = MakePool(HostState.Down);
        pool.Seed(HostState.Responding);
        pool.Start();

        Assert.Equal(HostState.Down, pool.CurrentState);
        Assert.Equal(HostState.Responding, Assert.Single(transitions).From);
    }

    private static (VerdictCoordinator verdict, Dictionary<Guid, TargetMonitor> monitors,
        TargetGraph graph, List<TargetTransition> transitions, FakeTimeProvider time)
        MakeVerdictWorld(HostState? monitorSeed)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        var graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Quad9"] }],
        });
        var monitors = new Dictionary<Guid, TargetMonitor>();
        foreach (var target in graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Profile, new PingDiscipline(Profile, time),
                time, NullLoggerFactory.Instance);
            if (monitorSeed is { } s)
                monitor.Seed(s);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitors[target.Id] = monitor;
        }
        var verdict = new VerdictCoordinator(graph, graph.GetPoolByName("Internet"), monitors,
            weatherThreshold: 2, time, NullLogger<VerdictCoordinator>.Instance);
        var transitions = new List<TargetTransition>();
        verdict.TransitionSource.Subscribe(transitions.Add);
        return (verdict, monitors, graph, transitions, time);
    }

    [Fact]
    public void Seeded_verdict_consistent_with_seeded_monitors_stays_silent()
    {
        var (verdict, _, _, transitions, _) = MakeVerdictWorld(HostState.Responding);
        verdict.Seed(VerdictState.Healthy);
        verdict.Start();

        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        Assert.Empty(transitions);
    }

    [Fact]
    public void Seeded_verdict_contradicted_by_monitor_states_corrects_once()
    {
        var (verdict, _, _, transitions, _) = MakeVerdictWorld(HostState.Responding);
        verdict.Seed(VerdictState.LinkDown);
        verdict.Start();

        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        var t = Assert.Single(transitions);
        Assert.Equal("Healthy", t.Reason.Verdict);
    }

    [Fact]
    public void An_unseeded_verdict_over_presumed_monitors_classifies_at_start()
    {
        // A new anchor pool over carried-over targets: no verdict seed, and no
        // monitor transition will ever come to classify it.
        var (verdict, _, _, transitions, _) = MakeVerdictWorld(HostState.Responding);
        verdict.Start();

        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        Assert.Equal("Healthy", Assert.Single(transitions).Reason.Verdict);
    }

    [Fact]
    public void A_cold_verdict_start_stays_silent()
    {
        var (verdict, _, _, transitions, _) = MakeVerdictWorld(monitorSeed: null);
        verdict.Start();

        Assert.Equal(VerdictState.Unknown, verdict.CurrentVerdict);
        Assert.Empty(transitions);
    }

    [Fact]
    public async Task A_pool_added_by_a_warm_rebuild_gets_its_state_and_a_verdict_at_once()
    {
        // The rebuild that adds the anchor pool carries every target over as
        // Responding but has no pool or verdict to carry. Fresh pings agree
        // with the presumption, so they never move either one.
        var router = Guid.NewGuid();
        var google = Guid.NewGuid();
        var cloudflare = Guid.NewGuid();
        var options = new WatcherOptions
        {
            Profiles = { ["Test"] = new PingProfileConfig { NormalIntervalSeconds = 60, HistorySize = 20, MinUsableSample = 5 } },
            Targets =
            [
                new TargetConfig { Id = router, Name = "Router", Address = "192.168.1.1", Profile = "Test" },
                new TargetConfig { Id = google, Name = "Google", Address = "8.8.8.8", Parent = "Router", Profile = "Test" },
                new TargetConfig { Id = cloudflare, Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router", Profile = "Test" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        };
        var seed = new EngineSeed
        {
            Targets = new Dictionary<Guid, TargetSeed>
            {
                [router] = new TargetSeed(HostState.Responding, "192.168.1.1"),
                [google] = new TargetSeed(HostState.Responding, "8.8.8.8"),
                [cloudflare] = new TargetSeed(HostState.Responding, "1.1.1.1"),
            },
        };
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Options.Create(options), Options.Create(new VerdictOptions { AnchorPool = "Internet" }));
        using var cts = new CancellationTokenSource();
        try
        {
            await engine.StartAsync(cts.Token, seed);

            Assert.Equal(HostState.Responding, Assert.Single(engine.PoolCoordinators).CurrentState);
            Assert.Equal(VerdictState.Healthy, engine.Verdict!.CurrentVerdict);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public void Seeded_verdict_outranks_unknown_when_no_monitor_is_determined()
    {
        var (verdict, _, _, transitions, _) = MakeVerdictWorld(monitorSeed: null);
        verdict.Seed(VerdictState.Healthy);
        verdict.Start();

        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        Assert.Empty(transitions);
    }
}
