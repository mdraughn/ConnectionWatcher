using System.Net;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using ConEngine.Tracking;

namespace ConEngine.Tests;

/// <summary>
/// The wiring in <see cref="WatcherEngine.StartAsync"/> (EV2, EV10, ET4, CF2 / ET8): the
/// coordinators are unit-tested by building them by hand, so these pin what only the
/// engine does — the probe-only veto, the ISP fallback, the DNS split, the reachability
/// gate on resolver notes, the scope gate on suspicion, and leaving disabled targets out.
/// </summary>
public class EngineWiringTests
{
[Fact]
    public async Task Every_anchor_isp_owned_raises_unprovable_up()
    {
        await using var h = EngineHarness.Home(home =>
        {
            home.Targets.Single(t => t.Name == "Google").Operator = "Comcast";
            home.Targets.Single(t => t.Name == "Cloudflare").Operator = "Comcast";
        });
        await h.StartAsync();

        Assert.True(h.Engine.Verdict!.UsingIspFallback);
        await TestWait.Eventually(
            () => h.Engine.ModelConditions.Any(c => c.Kind == ModelWarningKind.UnprovableUp),
            what: "UnprovableUp was never raised");
        Assert.Single(h.Engine.ModelConditions, c => c.Kind == ModelWarningKind.UnprovableUp);
    }

    [Fact]
    public async Task Dns_watch_splits_system_and_upstream_resolvers()
    {
        static void AddUpstream(HomeOptions home) => home.Targets.Add(new TargetConfig
        {
            Name = "Upstream", Address = "203.0.113.53", Profile = "Fast", Probe = "dns",
            Scope = TargetScope.Resolver, SystemManaged = false,
        });

        await using (var h = EngineHarness.Home(AddUpstream))
        {
            await h.StartAsync();

            var watch = h.Engine.DnsWatch;
            Assert.True(watch.Enabled);
            Assert.Equal(["192.0.2.53"], watch.System.Select(m => m.Target.Address));
            Assert.Equal(["203.0.113.53"], watch.Upstream.Select(m => m.Target.Address));
        }

        await using (var h = EngineHarness.Home(home =>
        {
            AddUpstream(home);
            home.Verdict.DnsWatch = false;
        }))
        {
            await h.StartAsync();
            Assert.False(h.Engine.DnsWatch.Enabled);
        }
    }

    [Fact]
    public async Task Anchor_pool_lookup_is_case_insensitive_and_missing_means_no_verdict()
    {
        await using (var h = EngineHarness.Home(home => home.Verdict.AnchorPool = "internet"))
        {
            await h.StartAsync();
            Assert.NotNull(h.Engine.Verdict);
        }

        await using (var h = EngineHarness.Home(home => home.Verdict.AnchorPool = "nope"))
        {
            await h.StartAsync();
            Assert.Null(h.Engine.Verdict);
        }
    }

    [Fact]
    public async Task Resolver_notes_wait_for_a_reachable_internet()
    {
        await using var h = EngineHarness.Home(home => home.Targets.Add(new TargetConfig
        {
            Name = "Resolver2", Address = "192.0.2.54", Profile = "Fast", Probe = "dns",
            Scope = TargetScope.Resolver, SystemManaged = true,
        }));
        h.Pings.SetUp(h.Ip("Google"), false);
        h.Pings.SetUp(h.Ip("Cloudflare"), false);
        await h.StartAsync();

        await h.WaitVerdictAsync(VerdictState.LinkDown);
        await h.WaitStateAsync("Resolver2", HostState.Responding);
        h.Pings.SetUp(h.Ip("Resolver"), false);
        await h.WaitStateAsync("Resolver", HostState.Down);

        // Absence window, after the positive condition: one resolver Down and one
        // answering would be a ResolverPartial, but not while the internet is down.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Assert.DoesNotContain(h.Engine.ModelConditions, c => c.Kind == ModelWarningKind.ResolverPartial);
            await Task.Delay(25);
        }
        Assert.Equal(VerdictState.LinkDown, h.Engine.Verdict!.CurrentVerdict);

        h.Pings.SetUp(h.Ip("Google"), true);
        h.Pings.SetUp(h.Ip("Cloudflare"), true);
        var resolverId = h.Id("Resolver");
        await TestWait.Eventually(
            () => h.Engine.ModelConditions.Any(c =>
                c.Kind == ModelWarningKind.ResolverPartial && c.TargetId == resolverId),
            what: "ResolverPartial never appeared once the internet was reachable");
    }

    [Fact]
    public async Task The_probe_only_veto_is_wired()
    {
        // The brief's recipe takes Google and Quad9 down; for that to be weather, Quad9
        // must be an anchor too (two degraded anchors, Cloudflare still answering).
        // Real time, not fake: the controllers pace on the engine clock, and the
        // injected windows are dated an hour ahead of it either way.
        await using var h = EngineHarness.Home(home =>
        {
            home.Pools.Single(p => p.Name == "Internet").Members.Add("Quad9");
            // 75 % success (every 4th ping dropped) is Faltering under this profile.
            home.Profiles["Lossy"] = new PingProfileConfig
            {
                NormalIntervalSeconds = 0.02,
                BurstIntervalSeconds = 0.01,
                BurstMaxDurationSeconds = 0.2,
                PingTimeoutSeconds = 0.5,
                HistorySize = 10,
                MinUsableSample = 5,
                RespondingThreshold = 0.90,
                FalteringThreshold = 0.85,
            };
            home.Targets.Single(t => t.Name == "Modem").Profile = "Lossy";
        });
        h.Pings.DropEveryNth(h.Ip("Modem"), 4);
        await h.StartAsync();

        var modemId = h.Id("Modem");
        await h.WaitStateAsync("Modem", HostState.Faltering);
        await h.WaitVerdictAsync(VerdictState.Healthy);

        // Ten closed windows, an hour ahead of the engine clock: the Modem loses 25 % of
        // its own probes while everything beyond it loses none. Live samples that follow
        // land in windows already closed and are dropped, so these ten stay the last ten.
        var loss = h.Engine.Loss;
        var first = LossWindows.WindowStart(h.Time.GetUtcNow() + TimeSpan.FromHours(1));
        string[] beyond = ["Google", "Cloudflare", "Quad9"];
        for (int w = 0; w < ModelCheckCoordinator.ProbeOnlyWindows; w++)
        {
            var start = first + LossWindows.Span * w;
            for (int i = 0; i < 20; i++)
            {
                var at = start + TimeSpan.FromSeconds(2 * i);
                loss.Record(modemId, at, success: i % 4 != 3);
                foreach (var name in beyond)
                    loss.Record(h.Id(name), at, success: true);
            }
        }
        // One sample past the last window's end plus the close delay closes it.
        loss.Record(h.Id("Google"),
            first + LossWindows.Span * ModelCheckCoordinator.ProbeOnlyWindows + LossWindows.CloseDelay, true);

        await TestWait.Eventually(
            () => h.Engine.ModelCheck!.Holds(ModelWarningKind.ProbeOnlyLoss, modemId),
            what: "the model check never raised ProbeOnlyLoss on the Modem");

        h.Pings.SetUp(h.Ip("Google"), false);
        h.Pings.SetUp(h.Ip("Quad9"), false);
        await h.WaitStateAsync("Google", HostState.Down);
        await h.WaitStateAsync("Quad9", HostState.Down);
        await TestWait.Eventually(
            () => h.Engine.Verdict!.CurrentVerdict is VerdictState.InternetWeather or VerdictState.LinkDegraded,
            what: "two Down anchors never made the verdict weather or a degraded link");

        Assert.Equal(HostState.Faltering, h.Engine.Monitors[modemId].CurrentState);
        Assert.True(h.Engine.ModelCheck!.Holds(ModelWarningKind.ProbeOnlyLoss, modemId));
        var verdict = h.Engine.Verdict!.Current;
        Assert.True(verdict.State == VerdictState.InternetWeather,
            $"expected InternetWeather (the Faltering Modem is vetoed), got {verdict.State}: {verdict.Detail}");
    }

    [Fact]
    public async Task Resolver_samples_reach_the_dns_log_before_the_monitor()
    {
        // A slower resolver cadence: its Starting -> Responding transition (5 samples)
        // comes about a second after start, well after this test subscribes.
        await using var h = EngineHarness.Home(home =>
        {
            home.Profiles["Slow"] = new PingProfileConfig
            {
                NormalIntervalSeconds = 0.2,
                BurstIntervalSeconds = 0.2,
                BurstMaxDurationSeconds = 0.2,
                PingTimeoutSeconds = 0.5,
                HistorySize = 10,
                MinUsableSample = 5,
            };
            home.Targets.Single(t => t.Name == "Resolver").Profile = "Slow";
        });
        await h.StartAsync();

        var resolverId = h.Id("Resolver");
        var monitor = h.Engine.Monitors[resolverId];
        var seen = new List<(int Logged, long Pinged)>();
        using var sub = h.Engine.TransitionSource.Subscribe(t =>
        {
            if (t.Kind != TransitionSubjectKind.Target || t.TargetId != resolverId)
                return;
            var logged = h.Engine.DnsResults.Recent(resolverId).Count;
            var pinged = monitor.PingCount;
            // The log keeps only the last Keep results; past that the counts part ways.
            if (pinged <= ConEngine.Dns.DnsResultLog.Keep)
                lock (seen)
                    seen.Add((logged, pinged));
        });

        await TestWait.Eventually(() => { lock (seen) return seen.Count > 0; },
            what: "no Resolver transition was seen while its sample count was small");
        lock (seen)
            Assert.All(seen, s => Assert.Equal(s.Pinged, s.Logged));
    }

    /// <summary>The state machine judges the last MinUsableSample results: over Fast's 5,
    /// two losses (60 %) are Faltering, and a transition ends a glitch episode. Over 10
    /// they are 80 %, still Responding: a glitch, not a transition.</summary>
    private static void TwoLossesStayResponding(HomeOptions home) => home.Profiles["Fast"].MinUsableSample = 10;

    [Fact]
    public async Task Anchor_glitches_reach_the_engine_glitch_stream()
    {
        await using var h = EngineHarness.Home(TwoLossesStayResponding);
        await h.StartAsync();
        await h.WaitVerdictAsync(VerdictState.Healthy);
        await h.WaitStateAsync("Google", HostState.Responding);

        h.Pings.Script(h.Ip("Google"), false, true, false);
        var googleId = h.Id("Google");
        await TestWait.Eventually(
            () => h.Glitches.Any(g => g.Class == GlitchClass.Target && g.TargetId == googleId && g.LostCount == 2),
            what: "Google's two-loss glitch never reached the engine's glitch stream");
    }

    [Fact]
    public async Task Local_targets_open_no_glitch_and_start_no_sweep()
    {
        await using var h = EngineHarness.Home(TwoLossesStayResponding);
        await h.StartAsync();
        await h.WaitVerdictAsync(VerdictState.Healthy);
        await h.WaitStateAsync("NAS", HostState.Responding);

        var nas = h.Ip("NAS");
        var before = h.Pings.PingCount(nas);
        // The same shape that makes an anchor glitch: two losses, then enough
        // successes (5) to close an episode, with margin.
        h.Pings.Script(nas, false, true, false, true, true, true, true, true, true);
        await TestWait.Eventually(() => h.Pings.PingCount(nas) >= before + 12,
            what: "the NAS never got past its script");

        // Absence window, after the positive condition.
        var anchors = new[] { "Google", "Cloudflare" }.Select(n => h.Engine.Monitors[h.Id(n)]).ToList();
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            Assert.DoesNotContain(anchors, a => a.Discipline.IsBurstActive);
            await Task.Delay(10);
        }
        var nasId = h.Id("NAS");
        Assert.DoesNotContain(h.Glitches, g => g.TargetId == nasId);
        // The NAS never left Responding, so an episode, had one opened, would have
        // closed as a glitch rather than been handed to the transition ledger.
        Assert.DoesNotContain(h.Transitions, t => t.TargetId == nasId && t.From == HostState.Responding);
    }

    [Fact]
    public async Task Disabled_targets_are_not_probed_pooled_or_watched()
    {
        await using var h = EngineHarness.Home(home =>
        {
            home.Profiles["Minute"] = new PingProfileConfig { NormalIntervalSeconds = 60, PingTimeoutSeconds = 0.5 };
            var cloudflare = home.Targets.Single(t => t.Name == "Cloudflare");
            cloudflare.Enabled = false;
            cloudflare.Profile = "Minute";
            home.Targets.Single(t => t.Name == "Resolver").Enabled = false;
        });
        await h.StartAsync();

        var cloudflareId = h.Id("Cloudflare");
        var resolverId = h.Id("Resolver");
        Assert.False(h.Engine.Monitors.ContainsKey(cloudflareId));
        Assert.False(h.Engine.Monitors.ContainsKey(resolverId));

        await TestWait.Eventually(() => h.Pings.PingCount(h.Ip("Google")) >= 5,
            what: "Google was never probed");
        var probed = h.Pings.Created.Select(c => c.Ip).ToList();
        Assert.DoesNotContain(h.Ip("Cloudflare"), probed);
        Assert.DoesNotContain(h.Ip("Resolver"), probed);

        var pool = h.Engine.PoolCoordinators.Single(p => p.Pool.Name == "Internet");
        await TestWait.Eventually(() => pool.CurrentState == HostState.Responding,
            what: "the Internet pool never reached Responding");
        var poolDetail = h.Transitions.Last(t => t.Kind == TransitionSubjectKind.Pool && t.TargetId == pool.Pool.Id)
            .Reason.Detail;
        // One member: the detail lists every member's state, and the disabled Cloudflare is not one.
        Assert.Contains("Google", poolDetail);
        Assert.DoesNotContain("Cloudflare", poolDetail);

        Assert.Empty(h.Engine.DnsWatch.System);

        // Only Fast counts: three of its 20 ms intervals plus its 0.5 s timeout.
        Assert.Equal(TimeSpan.FromSeconds(0.02) * 3 + TimeSpan.FromSeconds(0.5), h.Engine.StallAfter);
    }
}
