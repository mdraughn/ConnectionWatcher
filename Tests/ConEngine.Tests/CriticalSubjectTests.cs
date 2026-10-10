using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class CriticalSubjectTests
{
    private static WatcherOptions Options() => new()
    {
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal", Critical = true },
            new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
            new TargetConfig { Name = "NAS", Address = "192.168.1.20", Profile = "Internal",
                Scope = ConCore.Targets.TargetScope.Local, Critical = true },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google"] }],
    };

    [Fact]
    public async Task IsCriticalSubject_is_true_only_for_critical_targets()
    {
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.False(engine.IsCriticalSubject(Guid.NewGuid())); // before start: no graph, no throw

        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            Assert.True(engine.IsCriticalSubject(engine.Graph.GetByName("Router").Id));
            Assert.True(engine.IsCriticalSubject(engine.Graph.GetByName("NAS").Id)); // Local can be critical
            Assert.False(engine.IsCriticalSubject(engine.Graph.GetByName("Google").Id));
            Assert.False(engine.IsCriticalSubject(engine.Graph.GetPoolByName("Internet").Id));
            Assert.False(engine.IsCriticalSubject(VerdictCoordinator.SubjectId));
            Assert.True(engine.Graph.GetByName("Router").Critical); // TargetGraph carries it
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
