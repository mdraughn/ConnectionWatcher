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
}
