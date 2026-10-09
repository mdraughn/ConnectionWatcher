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

/// <summary>Spec 2026-10-09 §4.4–4.5: Unreachable only on own evidence, the
/// shallowest unproven Down ancestor is the root cause, and a hop changing
/// state makes everything beyond it re-check at once.</summary>
public class TopologyCoordinatorTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _coordinator;
    private readonly List<TargetTransition> _transitions = new();

    private static readonly PingProfile Fast = new()
    {
        Name = "Fast", HistorySize = 20, MinUsableSample = 5,
    };

    // Router -> Inside -> Edge -> {Google, Cloudflare}; Router -> Switch (a side branch).
    public TopologyCoordinatorTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Inside", Address = "10.1.10.1", Parent = "Router", Profile = "Internal" },
                new TargetConfig { Name = "Edge", Address = "203.0.113.1", Parent = "Inside", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Edge" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Edge" },
                new TargetConfig { Name = "Switch", Address = "192.168.1.228", Parent = "Router", Profile = "Internal" },
            ],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitor.TransitionSource.Subscribe(t => { lock (_transitions) _transitions.Add(t); });
            _monitors[target.Id] = monitor;
        }
        _coordinator = new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance);
        _coordinator.Start();
    }

    private TargetMonitor M(string name) => _monitors[_graph.GetByName(name).Id];
    private Guid Id(string name) => _graph.GetByName(name).Id;
    private HostState State(string name) => M(name).CurrentState;

    private void Feed(string name, int count, bool success)
    {
        var monitor = M(name);
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    private void AllHealthy()
    {
        foreach (var name in new[] { "Router", "Inside", "Edge", "Google", "Cloudflare", "Switch" })
            Feed(name, 5, true);
    }

    [Fact]
    public void A_hop_down_while_everything_beyond_answers_parks_nothing()
    {
        // The 2026-10-08 incident: Inside's probe fails, traffic passes it.
        AllHealthy();
        Feed("Inside", 20, false);
        Feed("Edge", 5, true);
        Feed("Google", 5, true);
        Feed("Cloudflare", 5, true);

        Assert.Equal(HostState.Down, State("Inside"));
        Assert.Equal(HostState.Responding, State("Edge"));
        Assert.Equal(HostState.Responding, State("Google"));
        Assert.Equal(HostState.Responding, State("Cloudflare"));
        Assert.True(_coordinator.ProvenForwarding(Id("Inside")));
        Assert.Null(_coordinator.RootCause(Id("Google")));
        lock (_transitions)
            Assert.DoesNotContain(_transitions, t => t.To == HostState.Unreachable);
    }

    // Failure order matters for WHICH root a target names on the way: while
    // the side-branch Switch still answers, the router is proven to forward and
    // a mid-path hop is the root cause. Failing the Switch first makes the
    // router the root the moment the last target beyond it goes quiet.
    private static readonly string[] BeyondRouter = { "Switch", "Inside", "Edge", "Google", "Cloudflare" };

    [Fact]
    public void A_dead_router_explains_every_descendant_that_proves_itself_down()
    {
        AllHealthy();
        Feed("Router", 20, false);
        foreach (var name in BeyondRouter)
            Feed(name, 20, false);

        Assert.Equal(HostState.Down, State("Router"));
        foreach (var name in new[] { "Inside", "Edge", "Google", "Cloudflare", "Switch" })
        {
            Assert.Equal(HostState.Unreachable, State(name));
            Assert.Equal(Id("Router"), M(name).ExplainedBy); // the shallowest, not the nearest
        }
        lock (_transitions)
            Assert.All(_transitions.Where(t => t.To == HostState.Unreachable), t =>
            {
                Assert.Equal(TransitionTrigger.ParentDown, t.Reason.Trigger);
                Assert.Equal(Id("Router"), t.Reason.AncestorId);
            });
    }

    [Fact]
    public void A_descendant_that_still_answers_is_never_unreachable_even_behind_a_dead_hop()
    {
        AllHealthy();
        Feed("Router", 20, false);
        Feed("Switch", 20, true); // answers: the router forwards to it

        Assert.Equal(HostState.Responding, State("Switch"));
        Assert.Null(M("Switch").ExplainedBy);
    }

    [Fact]
    public void A_bypassed_hop_explains_nothing_so_a_down_descendant_is_its_own_problem()
    {
        AllHealthy();
        Feed("Inside", 20, false);    // Inside not answering…
        Feed("Edge", 5, true);        // …but Edge answers: Inside is bypassed
        Feed("Google", 20, false);    // Google really is down

        Assert.Equal(HostState.Down, State("Google"));
        Assert.Null(M("Google").ExplainedBy);
    }

    [Fact]
    public void When_proof_of_forwarding_appears_an_explained_target_becomes_down_on_its_own()
    {
        AllHealthy();
        Feed("Inside", 20, false);
        Feed("Edge", 20, false);
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);
        Assert.Equal(HostState.Unreachable, State("Google"));

        Feed("Cloudflare", 5, true); // something beyond Inside answers again
        Assert.Equal(HostState.Down, State("Google"));
        Assert.Null(M("Google").ExplainedBy);
    }

    [Fact]
    public void Root_cause_recovery_releases_its_explained_descendants_and_bursts_them()
    {
        AllHealthy();
        Feed("Router", 20, false);
        foreach (var name in BeyondRouter)
            Feed(name, 20, false);

        Feed("Router", 10, true); // Down -> Recovering
        foreach (var name in new[] { "Inside", "Edge", "Google", "Cloudflare", "Switch" })
        {
            Assert.Equal(HostState.Starting, State(name));
            Assert.True(M(name).Discipline.IsBurstActive);
        }
    }

    [Fact]
    public void A_hop_starting_to_fail_bursts_everything_beyond_it_at_once()
    {
        AllHealthy();
        _time.Advance(TimeSpan.FromMinutes(5)); // all earlier bursts and guards long expired
        Feed("Inside", 2, false);               // Responding -> Faltering
        Assert.Equal(HostState.Faltering, State("Inside"));
        Assert.True(M("Edge").Discipline.IsBurstActive);
        Assert.True(M("Google").Discipline.IsBurstActive);
        Assert.False(M("Switch").Discipline.IsBurstActive); // not beyond Inside
    }

    [Fact]
    public void Recomputation_from_two_threads_converges_on_the_final_states()
    {
        AllHealthy();
        // Two threads drive opposite halves of a router failure at once.
        Parallel.Invoke(
            () => { Feed("Router", 20, false); Feed("Google", 20, false); },
            () => { Feed("Cloudflare", 20, false); Feed("Edge", 20, false); });
        Feed("Inside", 20, false);
        Feed("Switch", 20, false);

        foreach (var name in new[] { "Inside", "Edge", "Google", "Cloudflare", "Switch" })
            Assert.Equal(Id("Router"), M(name).ExplainedBy);
    }
}
