using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-10 R2: Resolver targets are tracked on the side, like Local ones.</summary>
public class ResolverScopeEngineTests
{
    private static WatcherOptions Options() => new()
    {
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
            new TargetConfig { Name = "Google", Address = "198.51.100.8", Parent = "Router" },
            new TargetConfig { Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local },
            new TargetConfig
            {
                Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Internal",
                Probe = "dns", Scope = TargetScope.Resolver,
            },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google"] }],
    };

    [Fact]
    public async Task Resolver_samples_stay_out_of_the_loss_windows_and_the_resolver_is_a_side_subject()
    {
        var pings = new FakePingTransceiverFactory();
        var engine = new WatcherEngine(TimeProvider.System, pings,
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.False(engine.IsSideSubject(Guid.NewGuid())); // before start: no graph, no throw

        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var resolverId = engine.Graph.GetByName("Your resolver 203.0.113.1").Id;
            var routerId = engine.Graph.GetByName("Router").Id;
            Assert.True(engine.IsSideSubject(resolverId));
            Assert.True(engine.IsSideSubject(engine.Graph.GetByName("NAS").Id));
            Assert.False(engine.IsSideSubject(routerId));
            Assert.False(engine.IsSideSubject(engine.Graph.GetPoolByName("Internet").Id));
            Assert.False(engine.IsSideSubject(VerdictCoordinator.SubjectId));
            Assert.False(engine.IsLocalSubject(resolverId)); // Resolver is not Local

            // The resolver's probe was created with the dns spec, and it is being pinged.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (engine.Monitors[resolverId].PingCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.True(engine.Monitors[resolverId].PingCount > 0);
            Assert.Contains(pings.Created, c => c.Probe == ProbeSpec.Dns);

            // Its samples never land in a loss window; the router's do.
            while (engine.Loss.Open(routerId) is not { Samples: > 0 } && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.True(engine.Loss.Open(routerId) is { Samples: > 0 });
            Assert.Null(engine.Loss.Open(resolverId));
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
