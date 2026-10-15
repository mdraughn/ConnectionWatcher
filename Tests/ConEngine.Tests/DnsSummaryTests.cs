using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Dns;
using ConEngine.Scheduling;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class DnsSummaryTests
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-14T00:00:00Z"));
    private readonly DnsResultLog _log = new();

    private TargetMonitor Monitor(string name, string address, bool managed)
    {
        var target = new Target
        {
            Id = Guid.NewGuid(), Name = name, Address = address, ProfileName = "Fast",
            Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = managed,
        };
        var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
        monitor.StartTracking(IPAddress.Parse(address));
        return monitor;
    }

    private void Feed(TargetMonitor monitor, int count, bool success, string message = "Verified (dns.google)")
    {
        for (var i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            var result = new PingResult(monitor.ResolvedAddress!, success, success ? 5 : 0, message, _time.GetUtcNow());
            monitor.RecordPingResult(result);
            _log.Record(monitor.Target.Id, result);
        }
    }

    [Fact]
    public void Off_or_no_system_resolvers_is_not_watched_or_unknown()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", managed: true);
        Assert.Equal(new DnsSummary(false, DnsSummaryState.Unknown),
            DnsSummary.Compute(new DnsWatch(false, [system], []), _log));
        Assert.Equal(new DnsSummary(false, DnsSummaryState.Unknown),
            DnsSummary.Compute(new DnsWatch(true, [], []), _log));
        Assert.Equal(new DnsSummary(true, DnsSummaryState.Unknown),
            DnsSummary.Compute(new DnsWatch(true, [system], []), _log)); // Starting: undetermined
    }

    [Fact]
    public void All_answering_is_Ok_some_down_is_Degraded_all_down_is_Down()
    {
        var a = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        var b = Monitor("Your resolver 10.0.0.2", "10.0.0.2", true);
        Feed(a, 10, true); Feed(b, 10, true);
        Assert.Equal(DnsSummaryState.Ok, DnsSummary.Compute(new DnsWatch(true, [a, b], []), _log).State);

        Feed(b, 20, false, "TimedOut (dns.google)");
        Assert.Equal(HostState.Down, b.CurrentState);
        Assert.Equal(DnsSummaryState.Degraded, DnsSummary.Compute(new DnsWatch(true, [a, b], []), _log).State);

        Feed(a, 20, false, "TimedOut (dns.google)");
        Assert.Equal(DnsSummaryState.Down, DnsSummary.Compute(new DnsWatch(true, [a, b], []), _log).State);
    }

    [Fact]
    public void A_faltering_resolver_is_Degraded_and_upstream_resolvers_never_change_the_state()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        var upstream = Monitor("1.1.1.1 directly", "1.1.1.1", false);
        Feed(system, 10, true);
        Feed(upstream, 20, false, "TimedOut (dns.google)");
        Assert.Equal(DnsSummaryState.Ok, DnsSummary.Compute(new DnsWatch(true, [system], [upstream]), _log).State);

        Feed(system, 4, false, "TimedOut (dns.google)"); // 6 of 10: Faltering
        Assert.Equal(HostState.Faltering, system.CurrentState);
        Assert.Equal(DnsSummaryState.Degraded, DnsSummary.Compute(new DnsWatch(true, [system], [upstream]), _log).State);
    }

    [Fact]
    public void A_resolver_that_blocks_the_seeded_names_is_Filtered()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        Feed(system, 10, true, "Filtered: 0.0.0.0 (dns.google)");
        Assert.Equal(HostState.Responding, system.CurrentState);
        Assert.Equal(DnsSummaryState.Filtered, DnsSummary.Compute(new DnsWatch(true, [system], []), _log).State);

        // The blocked name answers for real again: not a block any more.
        Feed(system, 1, true, "Verified (dns.google)");
        Assert.Equal(DnsSummaryState.Ok, DnsSummary.Compute(new DnsWatch(true, [system], []), _log).State);
    }

    [Fact]
    public void Hostname_target_answers_do_not_mask_a_block()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        for (var i = 0; i < 5; i++)
        {
            Feed(system, 1, true, "Filtered: NXDOMAIN (dns.google)");
            Feed(system, 1, true, "Answered (router.local)"); // an admin/hostname name: no expected set, no judgement
        }
        Assert.Equal(DnsSummaryState.Filtered, DnsSummary.Compute(new DnsWatch(true, [system], []), _log).State);
    }

    [Fact]
    public void Many_hostname_answers_between_the_blocks_never_dilute_the_judged_window()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        for (var i = 0; i < 4; i++)
        {
            Feed(system, 1, true, "Filtered: NXDOMAIN (dns.google)");
            Feed(system, 4, true, "Answered (router.local)");
        }
        Assert.Equal(DnsSummaryState.Filtered, DnsSummary.Compute(new DnsWatch(true, [system], []), _log).State);
    }

    [Fact]
    public void A_different_seeded_name_verifying_does_not_clear_a_block_on_another()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        for (var i = 0; i < 3; i++)
        {
            Feed(system, 1, true, "Filtered: NXDOMAIN (dns.google)");
            Feed(system, 1, true, "Verified (one.one.one.one)");
        }
        Assert.Equal(DnsSummaryState.Filtered, DnsSummary.Compute(new DnsWatch(true, [system], []), _log).State);
    }
}
