using System.Net;
using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

/// <summary>
/// Hostname targets follow DNS: an address that leaves the answer set is
/// replaced (state and history kept); an address still in the set is kept
/// even when the answer order rotates.
/// </summary>
public class DnsRefreshTests
{
    private static readonly IPAddress OldIp = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress NewIp = IPAddress.Parse("203.0.113.20");

    private static WatcherOptions HostnameTarget() => new()
    {
        Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
        Targets = [new TargetConfig { Name = "Site", Address = "site.example", Profile = "Fast" }],
    };

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(20);
        Assert.True(condition(), "Condition not met within timeout");
    }

    [Fact]
    public async Task Retired_address_is_replaced_and_the_target_keeps_responding()
    {
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Answers["site.example"] = [OldIp];
        var engine = new WatcherEngine(TimeProvider.System, pings, dns, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(HostnameTarget()),
            dnsRefreshInterval: TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var monitor = engine.Monitors[engine.Graph.GetByName("Site").Id];
            await Eventually(() => monitor.CurrentState == HostState.Responding);
            Assert.Equal(OldIp, monitor.ResolvedAddress);

            dns.Answers["site.example"] = [NewIp]; // the old address left the record set
            pings.SetUp(OldIp, up: false);          // and stopped answering

            await Eventually(() => Equals(monitor.ResolvedAddress, NewIp));
            await Eventually(() => pings.ObservedTimeouts.ContainsKey(NewIp));
            await Eventually(() => monitor.CurrentState == HostState.Responding);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Address_still_in_the_answer_set_is_kept()
    {
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Answers["site.example"] = [OldIp, NewIp];
        var engine = new WatcherEngine(TimeProvider.System, pings, dns, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(HostnameTarget()),
            dnsRefreshInterval: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var monitor = engine.Monitors[engine.Graph.GetByName("Site").Id];
            await Eventually(() => monitor.CurrentState == HostState.Responding);

            dns.Answers["site.example"] = [NewIp, OldIp]; // rotated order, same set
            await Eventually(() => dns.ResolveCount["site.example"] >= 4);

            Assert.Equal(OldIp, monitor.ResolvedAddress);
            Assert.False(pings.ObservedTimeouts.ContainsKey(NewIp));
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Refresh_failure_keeps_the_current_address()
    {
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Answers["site.example"] = [OldIp];
        var engine = new WatcherEngine(TimeProvider.System, pings, dns, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(HostnameTarget()),
            dnsRefreshInterval: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var monitor = engine.Monitors[engine.Graph.GetByName("Site").Id];
            await Eventually(() => monitor.CurrentState == HostState.Responding);

            dns.Answers.TryRemove("site.example", out _); // resolver now throws
            var before = dns.ResolveCount["site.example"];
            await Eventually(() => dns.ResolveCount["site.example"] >= before + 3);

            Assert.Equal(OldIp, monitor.ResolvedAddress);
            Assert.Equal(HostState.Responding, monitor.CurrentState);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Lookup_failure_reason_reaches_the_transition()
    {
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Failures["site.example"] = "Host not found";
        var engine = new WatcherEngine(TimeProvider.System, pings, dns, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(HostnameTarget()),
            dnsRefreshInterval: TimeSpan.FromMilliseconds(100));
        var transitions = new List<ConEngine.Events.TargetTransition>();
        engine.TransitionSource.Subscribe(t => { lock (transitions) transitions.Add(t); });
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            await Eventually(() =>
            {
                lock (transitions)
                    return transitions.Any(t =>
                        t.Reason.Trigger == ConEngine.Events.TransitionTrigger.ResolutionFailed &&
                        t.Reason.Detail!.Contains("Host not found"));
            });
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    private static WatcherEngine Build(FakePingTransceiverFactory pings, FakeDomainNameResolverFactory dns,
        TimeSpan refresh, TimeProvider? time = null) =>
        new(time ?? TimeProvider.System, pings, dns, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(HostnameTarget()), dnsRefreshInterval: refresh);

    [Fact]
    public async Task Going_down_re_resolves_immediately()
    {
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Answers["site.example"] = [OldIp];
        var engine = Build(pings, dns, TimeSpan.FromHours(1)); // only the Down trigger can beat this
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var monitor = engine.Monitors[engine.Graph.GetByName("Site").Id];
            await TestWait.Eventually(() => monitor.CurrentState == HostState.Responding, 15000, "Responding on OldIp");
            Assert.Equal(OldIp, monitor.ResolvedAddress);

            dns.Answers["site.example"] = [NewIp];
            pings.SetUp(OldIp, up: false);

            await TestWait.Eventually(() => Equals(monitor.ResolvedAddress, NewIp), 15000, "re-resolved to NewIp");
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Re_point_keeps_history_and_bursts()
    {
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Answers["site.example"] = [OldIp];
        var engine = Build(pings, dns, TimeSpan.FromHours(1));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var monitor = engine.Monitors[engine.Graph.GetByName("Site").Id];
            await TestWait.Eventually(() => monitor.CurrentState == HostState.Responding, 15000, "Responding on OldIp");
            await TestWait.Eventually(() => monitor.PingCount > 0, 15000, "pings recorded");
            var before = monitor.PingCount;

            dns.Answers["site.example"] = [NewIp];
            pings.SetUp(OldIp, up: false);

            // AUDIT: a Down target already bursts on its own, so this cannot tell RepointAsync's
            // TriggerBurst from the Down state's burst; removing that call leaves the test green.
            // The burst the re-point starts is still running right after it.
            await TestWait.Eventually(() => Equals(monitor.ResolvedAddress, NewIp) && monitor.Discipline.IsBurstActive,
                15000, "re-pointed with a burst running");
            // History is kept: the counter carries on from where it was.
            Assert.True(monitor.PingCount >= before && monitor.PingCount > 0,
                $"PingCount was reset: {monitor.PingCount} after {before}");
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Failing_resolution_backs_off_thirty_seconds_doubling()
    {
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Failures["site.example"] = "Host not found"; // never resolves: backoff 30 s, 60 s, 120 s ...
        var time = new TimerCountingTime();
        var engine = Build(pings, dns, TimeSpan.FromHours(1), time);
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            await TestWait.Eventually(() => dns.ResolveCount.GetValueOrDefault("site.example") == 1, 15000, "resolve #1");
            await time.WaitForTimersAsync(1);
            var created = time.TimersCreated;

            time.Advance(TimeSpan.FromSeconds(29));
            await Task.Delay(150); // absence check: nothing may resolve before +30 s
            Assert.Equal(1, dns.ResolveCount["site.example"]);

            time.Advance(TimeSpan.FromSeconds(1)); // +30 s
            await TestWait.Eventually(() => dns.ResolveCount["site.example"] == 2, 15000, "resolve #2 at +30 s");
            await time.NewTimerArmedSince(created);
            created = time.TimersCreated;

            time.Advance(TimeSpan.FromSeconds(59)); // +89 s: the second wait is 60 s, not 30
            await Task.Delay(150);
            Assert.Equal(2, dns.ResolveCount["site.example"]);

            time.Advance(TimeSpan.FromSeconds(1)); // +90 s
            await TestWait.Eventually(() => dns.ResolveCount["site.example"] == 3, 15000, "resolve #3 at +90 s");
            await time.NewTimerArmedSince(created);

            // And the cap: 30, 60, 120, 240, then 300 s, never more.
            foreach (var (wait, expected) in new[] { (120, 4), (240, 5), (300, 6), (300, 7) })
            {
                created = time.TimersCreated;
                time.Advance(TimeSpan.FromSeconds(wait - 1));
                await Task.Delay(100);
                Assert.Equal(expected - 1, dns.ResolveCount["site.example"]);
                time.Advance(TimeSpan.FromSeconds(1));
                var n = expected;
                await TestWait.Eventually(() => dns.ResolveCount["site.example"] == n, 15000, $"resolve #{n}");
                await time.NewTimerArmedSince(created);
            }
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
