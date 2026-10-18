using ConEngine.Configuration;
using ConEngine.Dns;
using ConEngine.Topology;

namespace ConEngine.Tests;

/// <summary>
/// The ruled design constants. Other tests reference these constants rather than
/// pinning their values, so a refactor could change one with every test green.
/// This is the one place the values are written down.
/// </summary>
public class DesignConstantsTests
{
    [Fact]
    public void Design_constants_match_the_specs()
    {
        // Verdict path-vs-anchors design (2026-10-08): anchors get at most 30 s to answer.
        Assert.Equal(TimeSpan.FromSeconds(30), VerdictCoordinator.AnchorWaitCap);

        // Verdict design: a Faltering hop is held for 2 min; a hop that never answered is
        // reported after 5 min.
        Assert.Equal(TimeSpan.FromMinutes(2), ModelCheckCoordinator.FalteringHold);
        Assert.Equal(TimeSpan.FromMinutes(5), ModelCheckCoordinator.NeverAnsweredAfter);

        // ProbeOnlyLoss design (Plan C): ten loss windows; raise at 15 %, clear at 10 %;
        // forwarding is raised below 2 % and cleared above 5 %.
        Assert.Equal(10, ModelCheckCoordinator.ProbeOnlyWindows);
        Assert.Equal(0.15, ModelCheckCoordinator.ProbeOnlyRaise);
        Assert.Equal(0.10, ModelCheckCoordinator.ProbeOnlyClear);
        Assert.Equal(0.02, ModelCheckCoordinator.ForwardingRaise);
        Assert.Equal(0.05, ModelCheckCoordinator.ForwardingClear);

        // DNS watch design (2026-10-10): resolver window 10, raise at 3 misses, name misses 3.
        // Controller ruling from the audit; the code agrees.
        Assert.Equal(10, ModelCheckCoordinator.ResolverWindow);
        Assert.Equal(3, ModelCheckCoordinator.ResolverRaise);
        Assert.Equal(3, ModelCheckCoordinator.NameMisses);

        // DNS watch design: the last 20 results per Resolver target are kept.
        Assert.Equal(20, DnsResultLog.Keep);

        // Verdict design: internet weather needs 2 degraded targets.
        Assert.Equal(2, new VerdictOptions().WeatherThreshold);

        // The PingDiscipline 60 s suspicion-burst guard is private; it is pinned through its
        // observable effect by PingDisciplineTests.Suspicion_burst_blocked_within_60s_of_last_burst_start.
    }
}
