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
}
