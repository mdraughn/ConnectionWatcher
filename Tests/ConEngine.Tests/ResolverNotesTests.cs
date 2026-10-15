using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Dns;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-10 §4.5: the resolver notes. Admin-facing; never the verdict.</summary>
public class ResolverNotesTests : IDisposable
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-14T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly DnsResultLog _log = new();
    private readonly List<ModelConditionChange> _changes = new();
    private ModelCheckCoordinator _check = null!;

    public ResolverNotesTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Google", Address = "8.8.8.8" },
                new TargetConfig { Name = "Your resolver 10.0.0.1", Address = "10.0.0.1", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true },
                new TargetConfig { Name = "Your resolver 10.0.0.2", Address = "10.0.0.2", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true },
                new TargetConfig { Name = "1.1.1.1 directly", Address = "1.1.1.1", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver },
            ],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
    }

    private TargetMonitor M(string name) => _monitors[_graph.GetByName(name).Id];

    private void Start(Func<bool>? internetReachable = null)
    {
        var dns = new DnsWatch(true, [M("Your resolver 10.0.0.1"), M("Your resolver 10.0.0.2")], [M("1.1.1.1 directly")]);
        _check = new ModelCheckCoordinator(_graph, _monitors, false, _time, NullLogger<ModelCheckCoordinator>.Instance,
            null, dns, _log, internetReachable);
        _check.ChangeSource.Subscribe(_changes.Add);
        _check.Start();
    }

    private void Feed(string name, int count, bool success, string message)
    {
        var monitor = M(name);
        for (var i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            var result = new PingResult(monitor.ResolvedAddress!, success, success ? 5 : 0, message, _time.GetUtcNow());
            monitor.RecordPingResult(result);
            _log.Record(monitor.Target.Id, result);
        }
        _check.Evaluate();
    }

    private void AllAnswer()
    {
        Feed("Your resolver 10.0.0.1", 10, true, "Verified (dns.google)");
        Feed("Your resolver 10.0.0.2", 10, true, "Verified (dns.google)");
        Feed("1.1.1.1 directly", 10, true, "Verified (dns.google)");
    }

    private bool Active(ModelWarningKind kind, string? target = null) =>
        _check.Active.Any(c => c.Kind == kind && (target is null || c.TargetName == target));

    [Fact]
    public void One_of_two_system_resolvers_down_is_ResolverPartial()
    {
        Start(); AllAnswer();
        Feed("Your resolver 10.0.0.2", 20, false, "TimedOut (dns.google)");
        Assert.True(Active(ModelWarningKind.ResolverPartial, "Your resolver 10.0.0.2"));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ResolverPartial &&
            c.Message == "Your resolver 10.0.0.2 isn't answering; 10.0.0.1 is.");
        Feed("Your resolver 10.0.0.2", 20, true, "Verified (dns.google)");
        Assert.False(Active(ModelWarningKind.ResolverPartial));
    }

    [Fact]
    public void A_resolver_down_during_an_outage_raises_no_Down_based_note()
    {
        var reachable = false;
        Start(() => reachable);
        AllAnswer();
        Feed("Your resolver 10.0.0.2", 20, false, "TimedOut (dns.google)");
        Feed("1.1.1.1 directly", 20, false, "TimedOut (dns.google)");
        Assert.Equal(HostState.Down, M("Your resolver 10.0.0.2").CurrentState);
        Assert.False(Active(ModelWarningKind.ResolverPartial));
        Assert.False(Active(ModelWarningKind.UpstreamResolverDown));

        reachable = true;
        _check.Evaluate();
        Assert.True(Active(ModelWarningKind.ResolverPartial, "Your resolver 10.0.0.2"));
        Assert.True(Active(ModelWarningKind.UpstreamResolverDown, "1.1.1.1 directly"));
    }

    [Fact]
    public void Answered_hostname_results_never_dilute_the_lying_window()
    {
        Start(); AllAnswer();
        for (var i = 0; i < 3; i++)
        {
            Feed("Your resolver 10.0.0.1", 1, false, "WrongAnswer: 10.99.0.1 (dns.google)");
            Feed("Your resolver 10.0.0.1", 4, true, "Answered (router.local)");
        }
        Assert.True(Active(ModelWarningKind.ResolverLying, "Your resolver 10.0.0.1"));
    }

    [Theory]
    [InlineData("Filtered: NXDOMAIN (dns.google)", "NXDOMAIN")]
    [InlineData("WrongAnswer: 10.99.0.1, 10.99.0.2 (dns.google)", "10.99.0.1, 10.99.0.2")]
    [InlineData("WrongAnswer: 10.99.0.1", "10.99.0.1")]
    [InlineData("Filtered (dns.google)", "")]
    [InlineData("WrongAnswer", "")]
    public void DetailOf_strips_the_outcome_and_the_name_without_ever_throwing(string message, string detail)
    {
        var (outcome, name) = DnsResultLog.Parse(message);
        Assert.Equal(detail, ModelCheckCoordinator.DetailOf(new DnsResult(DateTimeOffset.UnixEpoch, false, outcome, name, message)));
    }

    [Fact]
    public void Wrong_answers_for_a_seeded_name_are_ResolverLying()
    {
        Start(); AllAnswer();
        Feed("Your resolver 10.0.0.1", 3, false, "WrongAnswer: 10.99.0.1 (dns.google)");
        Assert.True(Active(ModelWarningKind.ResolverLying, "Your resolver 10.0.0.1"));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ResolverLying &&
            c.Message == "Your resolver 10.0.0.1 answered dns.google with 10.99.0.1 — a captive portal or interception?");
    }

    [Fact]
    public void Blocked_seeded_names_are_ResolverFiltered_not_lying()
    {
        Start(); AllAnswer();
        Feed("Your resolver 10.0.0.1", 3, true, "Filtered: 0.0.0.0 (dns.google)");
        Assert.True(Active(ModelWarningKind.ResolverFiltered, "Your resolver 10.0.0.1"));
        Assert.False(Active(ModelWarningKind.ResolverLying));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ResolverFiltered &&
            c.Message == "Your resolver 10.0.0.1 blocks dns.google (answered 0.0.0.0) — a Pi-hole or AdGuard blocklist? " +
                         "Name resolution still works; the seeded lookup names can't prove it. Add a lookup name your resolver allows.");
    }

    [Fact]
    public void An_upstream_resolver_down_while_a_system_one_answers_is_UpstreamResolverDown()
    {
        Start(); AllAnswer();
        Feed("1.1.1.1 directly", 20, false, "TimedOut (dns.google)");
        Assert.True(Active(ModelWarningKind.UpstreamResolverDown, "1.1.1.1 directly"));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.UpstreamResolverDown &&
            c.Message == "1.1.1.1 isn't answering direct queries; your resolver is fine.");
        Assert.False(Active(ModelWarningKind.ResolverPartial));
    }

    [Fact]
    public void A_name_that_misses_everywhere_while_others_hit_is_NameUnresolvable()
    {
        Start(); AllAnswer();
        foreach (var r in new[] { "Your resolver 10.0.0.1", "Your resolver 10.0.0.2", "1.1.1.1 directly" })
            for (var i = 0; i < 3; i++)
            {
                Feed(r, 1, false, "ServFail (example.org)");
                Feed(r, 1, true, "Verified (dns.google)");
            }
        Assert.True(Active(ModelWarningKind.NameUnresolvable));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.NameUnresolvable && c.TargetId is null &&
            c.Message == "example.org isn't resolving anywhere.");
    }

    [Fact]
    public void Keys_are_stable_per_kind_and_target()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"resolver-partial:{id:D}", ModelCondition.KeyFor(ModelWarningKind.ResolverPartial, id));
        Assert.Equal($"resolver-lying:{id:D}", ModelCondition.KeyFor(ModelWarningKind.ResolverLying, id));
        Assert.Equal($"resolver-filtered:{id:D}", ModelCondition.KeyFor(ModelWarningKind.ResolverFiltered, id));
        Assert.Equal($"upstream-resolver-down:{id:D}", ModelCondition.KeyFor(ModelWarningKind.UpstreamResolverDown, id));
        Assert.Equal("name-unresolvable", ModelCondition.KeyFor(ModelWarningKind.NameUnresolvable, null));
    }

    public void Dispose() => _check?.Dispose();
}
