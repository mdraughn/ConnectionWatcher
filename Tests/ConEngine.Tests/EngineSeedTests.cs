using ConEngine.Events;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using ConEngine.Tracking;

namespace ConEngine.Tests;

/// <summary>
/// The engine half of warm restart (EV1 / ET2): <see cref="WatcherEngine.StartAsync"/>
/// applies seeds whose address and probe still match, cold-starts the rest, and
/// lands pool and verdict seeds without a transition. WarmStartTests covers the
/// monitor/coordinator halves; its only engine-level case asserts a seed is NOT
/// applied, which would also pass if seeds were ignored entirely.
/// </summary>
public class EngineSeedTests
{
    // Config ids are pinned so a seed can be built before the engine (and its graph) exists.
    private static readonly Guid RouterId = Guid.NewGuid();
    private static readonly Guid InternetId = Guid.NewGuid();
    private static readonly Guid ResolverId = Guid.NewGuid();
    private static readonly Guid GoogleId = Guid.NewGuid();
    private static readonly Guid CloudflareId = Guid.NewGuid();

    private static EngineHarness Home(double burstMaxSeconds = 5) => EngineHarness.Home(h =>
    {
        h.Profiles["Fast"].BurstMaxDurationSeconds = burstMaxSeconds;
        h.Targets.Single(t => t.Name == "Router").Id = RouterId;
        h.Targets.Single(t => t.Name == "Resolver").Id = ResolverId;
        h.Targets.Single(t => t.Name == "Google").Id = GoogleId;
        h.Targets.Single(t => t.Name == "Cloudflare").Id = CloudflareId;
        h.Pools.Single(p => p.Name == "Internet").Id = InternetId;
    });

    [Fact]
    public async Task A_matching_seed_is_assumed_bursts_and_confirms_silently()
    {
        // A long burst window keeps IsBurstActive true however slowly the runner gets here.
        await using var h = Home(burstMaxSeconds: 30);
        await h.StartAsync(new EngineSeed
        {
            Targets = { [RouterId] = new TargetSeed(HostState.Responding, "192.0.2.1", Probe: null) },
        });

        // Asserted synchronously after StartAsync; holds because the Router is an
        // IP literal, so it is activated (tracker started, burst triggered) before StartAsync returns.
        var router = h.Engine.Monitors[RouterId];
        Assert.True(router.IsAssumed);
        Assert.Equal(HostState.Responding, router.CurrentState);
        Assert.True(router.Discipline.IsBurstActive);

        await TestWait.Eventually(() => !router.IsAssumed, what: "Router never confirmed its presumed state");

        Assert.Equal(HostState.Responding, router.CurrentState);
        var transitions = h.Transitions;
        Assert.DoesNotContain(transitions, t => t.TargetId == RouterId);
    }

    [Fact]
    public async Task A_seed_whose_address_changed_cold_starts()
    {
        await using var h = Home();
        await h.StartAsync(new EngineSeed
        {
            Targets = { [RouterId] = new TargetSeed(HostState.Responding, "192.0.2.99", Probe: null) },
        });

        Assert.False(h.Engine.Monitors[RouterId].IsAssumed);
        await TestWait.Eventually(
            () => h.Transitions.Any(t => t.TargetId == RouterId &&
                                         t.From == HostState.Starting && t.To == HostState.Responding),
            what: "Router never made its cold-start Starting -> Responding transition");
    }

    [Fact]
    public async Task A_seed_whose_probe_changed_cold_starts()
    {
        // The seed says the Router was probed over tcp:80; config now probes it with ICMP (null).
        await using var h = Home();
        await h.StartAsync(new EngineSeed
        {
            Targets = { [RouterId] = new TargetSeed(HostState.Down, "192.0.2.1", Probe: "tcp:80") },
        });

        var router = h.Engine.Monitors[RouterId];
        Assert.False(router.IsAssumed);
        Assert.NotEqual(HostState.Down, router.CurrentState);
        await TestWait.Eventually(
            () => h.Transitions.Any(t => t.TargetId == RouterId &&
                                         t.From == HostState.Starting && t.To == HostState.Responding),
            what: "Router never made its cold-start Starting -> Responding transition");
    }

    [Fact]
    public async Task Pool_and_verdict_seeds_land_without_a_transition()
    {
        // The pool and verdict seeds come with their members' seeds, as a real warm start
        // has them. AUDIT: a pool seed alone is NOT silent. The pool recomputes from its
        // cold (Starting) members at once and emits Responding -> Starting. Pinned in the
        // test below; whether a seed should shield a pool from cold members is the question.
        await using var h = Home();
        await h.StartAsync(new EngineSeed
        {
            Targets =
            {
                [GoogleId] = new TargetSeed(HostState.Responding, "198.51.100.8"),
                [CloudflareId] = new TargetSeed(HostState.Responding, "198.51.100.1"),
            },
            Pools = { [InternetId] = HostState.Responding },
            Verdict = VerdictState.Healthy,
        });

        // Pool and verdict are applied inside StartAsync, before it returns.
        Assert.Equal(HostState.Responding, h.Engine.PoolCoordinators[0].CurrentState);
        Assert.Equal(VerdictState.Healthy, h.Engine.Verdict!.CurrentVerdict);

        // Positive condition first: the anchors have real samples behind them, so the
        // first reclassification has happened. Only then is a fixed absence window fair.
        await TestWait.Eventually(
            () => !h.Engine.Monitors[GoogleId].IsAssumed && !h.Engine.Monitors[CloudflareId].IsAssumed,
            what: "the anchors never confirmed their presumed state");
        await Task.Delay(TimeSpan.FromSeconds(2)); // absence window

        Assert.Equal(HostState.Responding, h.Engine.PoolCoordinators[0].CurrentState);
        Assert.Equal(VerdictState.Healthy, h.Engine.Verdict!.CurrentVerdict);
        var transitions = h.Transitions;
        Assert.DoesNotContain(transitions, t => t.Kind == TransitionSubjectKind.Pool);
        Assert.DoesNotContain(transitions, t => t.Kind == TransitionSubjectKind.Verdict);
    }

    [Fact]
    public async Task A_seeded_LinkDown_verdict_bursts_the_resolvers_when_it_clears()
    {
        await using var h = Home(burstMaxSeconds: 30);
        await h.StartAsync(new EngineSeed { Verdict = VerdictState.LinkDown });

        var resolver = h.Engine.Monitors[ResolverId];
        // The Resolver is not seeded, and it reaches Responding (which ends the warm-start
        // burst) before the seeded LinkDown clears; the burst seen after that is the
        // link-return one. Proven by mutation: without that burst this wait times out.
        await TestWait.Eventually(
            () => resolver.Discipline.IsBurstActive,
            what: "the Resolver never burst after the seeded LinkDown cleared");
    }
}
