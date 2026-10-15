using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Dns;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-10 §4.4: DnsDown fires only when the anchors prove the
/// internet reachable and every system resolver is Down.</summary>
public class VerdictDnsDownTests
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-14T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly List<TargetTransition> _transitions = new();
    private readonly DnsResultLog _dnsResults = new();
    private VerdictCoordinator _verdict = null!;

    public VerdictDnsDownTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
                new TargetConfig { Name = "Your resolver 192.168.1.1", Address = "192.168.1.1", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true },
                new TargetConfig { Name = "Your resolver 192.168.1.2", Address = "192.168.1.2", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true },
                new TargetConfig { Name = "9.9.9.9 directly", Address = "9.9.9.9", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance).Start();
    }

    private TargetMonitor M(string name) => _monitors[_graph.GetByName(name).Id];

    private void Start(bool watch = true, bool withUpstream = true)
    {
        var dns = new DnsWatch(watch,
            [M("Your resolver 192.168.1.1"), M("Your resolver 192.168.1.2")],
            withUpstream ? [M("9.9.9.9 directly")] : []);
        _verdict = new VerdictCoordinator(_graph, _graph.GetPoolByName("Internet"), _monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance, dns, _dnsResults);
        _verdict.TransitionSource.Subscribe(_transitions.Add);
        _verdict.Start();
    }

    private void Feed(string name, int count, bool success)
    {
        var monitor = M(name);
        for (var i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            Record(monitor, success);
        }
    }

    /// <summary>Like the engine: a Resolver-scope result lands in the log before the monitor sees it.</summary>
    private void Record(TargetMonitor monitor, bool success)
    {
        var result = new PingResult(monitor.ResolvedAddress!, success, success ? 5 : 0,
            success ? "Verified (dns.google)" : "TimedOut (dns.google)", _time.GetUtcNow());
        if (monitor.Target.Scope == TargetScope.Resolver)
            _dnsResults.Record(monitor.Target.Id, result);
        monitor.RecordPingResult(result);
    }

    /// <summary>One sample per round to each named monitor, one second per round.</summary>
    private void FeedTogether(int rounds, bool success, params string[] names)
    {
        for (var i = 0; i < rounds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            foreach (var name in names)
                Record(M(name), success);
        }
    }

    private void AnchorsUp() { Feed("Router", 10, true); Feed("Google", 10, true); Feed("Cloudflare", 10, true); }
    private void ResolversDown() { Feed("Your resolver 192.168.1.1", 20, false); Feed("Your resolver 192.168.1.2", 20, false); }

    [Fact]
    public void Anchors_up_and_every_system_resolver_down_is_DnsDown_blaming_your_resolver_when_the_upstream_answers()
    {
        Start();
        AnchorsUp();
        Feed("9.9.9.9 directly", 10, true);
        ResolversDown();

        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        var blame = _verdict.Current.Dns!;
        Assert.Equal(DnsBlameKind.YourResolver, blame.Kind);
        Assert.Equal("Your resolver 192.168.1.1 isn't answering, but 9.9.9.9 answers directly.", blame.Message);
        Assert.Equal(["192.168.1.1", "192.168.1.2"], blame.Resolvers);
        Assert.Contains(_transitions, t => t.To == HostState.Down && t.Reason.Verdict == "DnsDown");
        Assert.Null(_verdict.CurrentOperator);
    }

    [Fact]
    public void With_no_upstream_answering_the_blame_is_NoResolver()
    {
        Start();
        AnchorsUp();
        Feed("9.9.9.9 directly", 20, false);
        ResolversDown();
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        Assert.Equal(DnsBlameKind.NoResolver, _verdict.Current.Dns!.Kind);
        Assert.Equal("No resolver is answering, though the internet is reachable.", _verdict.Current.Dns.Message);
    }

    [Fact]
    public void With_no_upstream_configured_the_NoResolver_message_says_how_to_add_one()
    {
        Start(withUpstream: false);
        AnchorsUp();
        ResolversDown();
        Assert.EndsWith("Name a public resolver in Config → DNS so ConnectionWatcher can tell your resolver from everyone's.",
            _verdict.Current.Dns!.Message);
    }

    [Fact]
    public void One_system_resolver_still_answering_is_not_DnsDown()
    {
        Start();
        AnchorsUp();
        Feed("Your resolver 192.168.1.1", 10, true);
        Feed("Your resolver 192.168.1.2", 20, false);
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.Null(_verdict.Current.Dns);
    }

    [Fact]
    public void Anchors_down_with_resolvers_down_is_LinkDown_with_the_dns_suffix_never_DnsDown()
    {
        Start();
        AnchorsUp();
        Feed("Google", 20, false); Feed("Cloudflare", 20, false);
        ResolversDown();
        Assert.Equal(VerdictState.LinkDown, _verdict.CurrentVerdict);
        Assert.EndsWith(" — name lookups are failing too", _verdict.CurrentDetail);
        Assert.DoesNotContain(_transitions, t => t.Reason.Verdict == "DnsDown");
    }

    [Fact]
    public void Undetermined_resolvers_wait_for_the_cap_then_conclude()
    {
        Start();
        AnchorsUp();
        Feed("9.9.9.9 directly", 10, true);
        Feed("Your resolver 192.168.1.1", 20, false); // the other stays Starting
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.Null(_verdict.Current.Dns);
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        Assert.StartsWith("Your resolver 192.168.1.1 isn't answering", _verdict.Current.Dns!.Message);
    }

    [Fact]
    public void An_expired_anchor_wait_does_not_shorten_a_later_resolver_wait()
    {
        Start();
        Feed("Router", 10, true);
        Feed("Google", 20, false); // Cloudflare stays Starting: the anchor wait runs
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.LinkDown, _verdict.CurrentVerdict);

        // Resolver A goes Down while the verdict is LinkDown (B stays Starting); then Google's
        // recovery pass goes straight from the expired anchor wait into the resolver wait.
        Feed("Your resolver 192.168.1.1", 20, false);
        Feed("Google", 20, true);
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
    }

    [Fact]
    public void An_expired_resolver_wait_does_not_shorten_a_later_anchor_wait()
    {
        Start();
        Feed("Router", 10, true);
        Feed("Google", 10, true); // Cloudflare is never fed: it stays Starting
        Feed("Your resolver 192.168.1.1", 20, false); // B stays Starting
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);

        Feed("Google", 20, false);
        Assert.Equal(VerdictState.Unknown, _verdict.CurrentVerdict);
        Assert.Equal("Waiting for anchors to report", _verdict.CurrentDetail);
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.LinkDown, _verdict.CurrentVerdict);
    }

    [Fact]
    public void The_blame_names_the_first_resolver_that_is_Down_not_the_first_in_the_list()
    {
        Start();
        AnchorsUp();
        Feed("9.9.9.9 directly", 10, true);
        Feed("Your resolver 192.168.1.2", 20, false); // .1 stays Starting
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        Assert.Equal("Your resolver 192.168.1.2 isn't answering, but 9.9.9.9 answers directly.", _verdict.Current.Dns!.Message);
    }

    [Fact]
    public void DnsDown_then_anchors_down_is_LinkDown_with_the_suffix()
    {
        Start();
        AnchorsUp();
        ResolversDown();
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        Feed("Google", 20, false); Feed("Cloudflare", 20, false);
        Assert.Equal(VerdictState.LinkDown, _verdict.CurrentVerdict);
        Assert.EndsWith(" — name lookups are failing too", _verdict.CurrentDetail);
    }

    [Fact]
    public void A_resolver_recovering_during_the_wait_ends_it_cleanly()
    {
        Start();
        AnchorsUp();
        Feed("Your resolver 192.168.1.1", 20, false); // B stays Starting
        _time.Advance(TimeSpan.FromSeconds(10));
        Feed("Your resolver 192.168.1.1", 20, true);
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        var count = _transitions.Count;
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.Equal(count, _transitions.Count);
    }

    [Fact]
    public void A_watch_that_is_off_never_produces_DnsDown()
    {
        Start(watch: false);
        AnchorsUp();
        ResolversDown();
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
    }

    /// <summary>Final review C1: the link was down, so the resolvers' windows are full of
    /// the outage's failures. Anchors and resolvers start in sync, then the link goes.</summary>
    private void OutageWithEverythingDown()
    {
        AnchorsUp();
        Feed("Your resolver 192.168.1.1", 10, true);
        Feed("Your resolver 192.168.1.2", 10, true);
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        FeedTogether(20, false, "Google", "Cloudflare", "Your resolver 192.168.1.1", "Your resolver 192.168.1.2");
        Assert.Equal(VerdictState.LinkDown, _verdict.CurrentVerdict);
        Assert.Equal(HostState.Down, M("Your resolver 192.168.1.1").CurrentState);
        Assert.Equal(HostState.Down, M("Your resolver 192.168.1.2").CurrentState);
    }

    [Fact]
    public void Anchors_recovering_while_the_resolvers_are_still_down_never_passes_through_DnsDown()
    {
        Start();
        OutageWithEverythingDown();

        FeedTogether(20, true, "Google", "Cloudflare");
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.DoesNotContain(_transitions, t => t.Reason.Verdict == "DnsDown");

        Feed("Your resolver 192.168.1.1", 10, true);
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.DoesNotContain(_transitions, t => t.Reason.Verdict == "DnsDown");
    }

    [Fact]
    public void Fresh_answers_from_a_resolver_still_marked_Down_are_not_evidence_of_DnsDown()
    {
        Start();
        OutageWithEverythingDown();

        // The anchors come back first; both resolvers answer their first burst probes
        // but are still Down when another monitor's transition (the upstream resolver
        // proving itself) reclassifies.
        FeedTogether(20, true, "Google", "Cloudflare");
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        FeedTogether(1, true, "Your resolver 192.168.1.1", "Your resolver 192.168.1.2");
        Assert.Equal(HostState.Down, M("Your resolver 192.168.1.1").CurrentState);
        Assert.Equal(HostState.Down, M("Your resolver 192.168.1.2").CurrentState);
        Feed("9.9.9.9 directly", 6, true);
        Assert.Equal(HostState.Responding, M("9.9.9.9 directly").CurrentState);
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.DoesNotContain(_transitions, t => t.Reason.Verdict == "DnsDown");
    }

    [Fact]
    public void A_resolver_that_stays_dead_after_the_link_returns_is_DnsDown_after_the_cap()
    {
        Start();
        OutageWithEverythingDown();

        FeedTogether(20, true, "Google", "Cloudflare");
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        FeedTogether(5, false, "Your resolver 192.168.1.1", "Your resolver 192.168.1.2"); // fresh failures
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        Assert.StartsWith("No resolver is answering", _verdict.Current.Dns!.Message);
    }

    [Fact]
    public void A_resolver_with_no_fresh_evidence_is_still_concluded_DnsDown_at_the_cap()
    {
        Start();
        OutageWithEverythingDown();

        FeedTogether(20, true, "Google", "Cloudflare");
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
    }

    [Fact]
    public void Recovery_of_the_resolvers_returns_to_Healthy()
    {
        Start();
        AnchorsUp();
        ResolversDown();
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        Feed("Your resolver 192.168.1.1", 20, true);
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.Null(_verdict.Current.Dns);
    }
}
