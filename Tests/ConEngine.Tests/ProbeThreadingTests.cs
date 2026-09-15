using System.Net;
using ConCore;
using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class ProbeThreadingTests
{
    // Scaffolding copied from EngineExtensionTests.Engine_emits_ping_samples_with_target_ids:
    // one target "Modem" at 10.1.10.1 with Probe = "tcp:80", fast profile.
    [Fact]
    public async Task Tcp_target_probes_with_its_spec_and_reaches_responding()
    {
        var options = new WatcherOptions
        {
            Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
            Targets = [new TargetConfig { Name = "Modem", Address = "10.1.10.1", Profile = "Fast", Probe = "tcp:80" }],
        };
        var fake = new FakePingTransceiverFactory();
        var engine = new WatcherEngine(TimeProvider.System, fake,
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);

        var ip = IPAddress.Parse("10.1.10.1");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               engine.Monitors[engine.Graph.GetByName("Modem").Id].CurrentState != HostState.Responding)
        {
            await Task.Delay(20);
        }

        cts.Cancel();
        await engine.StopAsync();

        Assert.Equal(HostState.Responding,
            engine.Monitors[engine.Graph.GetByName("Modem").Id].CurrentState);
        Assert.Equal(new ProbeSpec(ProbeKind.Tcp, 80), fake.ObservedProbes[ip]);
    }

    // Regression for the probe-aware burst clamp in WatcherEngine.Activate:
    // burstCapMs is 2500 for TCP probes (Windows refusal latency needs the
    // headroom) and 1000 for everything else. Flip 2500 -> 1000 in Activate
    // and this test goes red on the TCP assertion.
    [Fact]
    public async Task Burst_clamps_tcp_timeout_higher_than_icmp()
    {
        var options = new WatcherOptions
        {
            Profiles =
            {
                ["Fast"] = new PingProfileConfig
                {
                    NormalIntervalSeconds = 0.02,
                    PingTimeoutSeconds = 5, // steady timeout well above either burst cap
                    BurstIntervalSeconds = 0.02,
                    BurstMaxDurationSeconds = 30,
                    MinUsableSample = 5,
                    HistorySize = 20,
                },
            },
            Targets =
            [
                new TargetConfig { Name = "TcpTarget", Address = "10.2.20.1", Profile = "Fast", Probe = "tcp:80" },
                new TargetConfig { Name = "IcmpTarget", Address = "10.2.20.2", Profile = "Fast" },
            ],
        };
        var fake = new FakePingTransceiverFactory();
        var engine = new WatcherEngine(TimeProvider.System, fake,
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);

        var tcpIp = IPAddress.Parse("10.2.20.1");
        var icmpIp = IPAddress.Parse("10.2.20.2");
        var tcpMonitor = engine.Monitors[engine.Graph.GetByName("TcpTarget").Id];
        var icmpMonitor = engine.Monitors[engine.Graph.GetByName("IcmpTarget").Id];

        // Wait until both controllers are actively pinging at the steady timeout.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               (!fake.ObservedTimeouts.ContainsKey(tcpIp) || !fake.ObservedTimeouts.ContainsKey(icmpIp)))
            await Task.Delay(20);

        tcpMonitor.Discipline.TriggerBurst();
        icmpMonitor.Discipline.TriggerBurst();

        // Let a burst-mode ping happen for each and observe its clamped timeout.
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               (!fake.ObservedTimeouts[tcpIp].Contains(2500) || !fake.ObservedTimeouts[icmpIp].Contains(1000)))
            await Task.Delay(20);

        cts.Cancel();
        await engine.StopAsync();

        Assert.Contains(2500, fake.ObservedTimeouts[tcpIp]);
        Assert.Contains(1000, fake.ObservedTimeouts[icmpIp]);
    }
}
