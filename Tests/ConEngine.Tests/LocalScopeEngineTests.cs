using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Scheduling;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class LocalScopeEngineTests
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };

    private static WatcherOptions Options() => new()
    {
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
            new TargetConfig { Name = "Google", Address = "198.51.100.8", Parent = "Router" },
            new TargetConfig { Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google"] }],
    };

    [Fact]
    public async Task IsLocalSubject_is_true_only_for_Local_targets()
    {
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.False(engine.IsLocalSubject(Guid.NewGuid())); // before start: no graph, no throw

        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            Assert.True(engine.IsLocalSubject(engine.Graph.GetByName("NAS").Id));
            Assert.False(engine.IsLocalSubject(engine.Graph.GetByName("Router").Id));
            Assert.False(engine.IsLocalSubject(engine.Graph.GetPoolByName("Internet").Id));
            Assert.False(engine.IsLocalSubject(VerdictCoordinator.SubjectId));
            Assert.False(engine.IsLocalSubject(Guid.NewGuid()));
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public void A_Local_target_going_down_does_not_change_the_verdict()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T00:00:00Z"));
        var graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.0.2.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "192.0.2.8", Parent = "Router" },
                new TargetConfig { Name = "Cloudflare", Address = "192.0.2.9", Parent = "Router" },
                new TargetConfig { Name = "NAS", Address = "192.0.2.50", Profile = "Local", Scope = TargetScope.Local },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        });
        var monitors = new Dictionary<Guid, TargetMonitor>();
        foreach (var target in graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, time), time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitors[target.Id] = monitor;
        }
        new TopologyCoordinator(graph, monitors, NullLogger<TopologyCoordinator>.Instance).Start();
        var verdict = new VerdictCoordinator(graph, graph.GetPoolByName("Internet"), monitors,
            weatherThreshold: 2, time, NullLogger<VerdictCoordinator>.Instance);
        var verdictTransitions = new List<ConEngine.Events.TargetTransition>();
        verdict.TransitionSource.Subscribe(verdictTransitions.Add);
        verdict.Start();

        void Feed(string name, int count, bool success)
        {
            var monitor = monitors[graph.GetByName(name).Id];
            for (int i = 0; i < count; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                    success ? 5 : 0, timestamp: time.GetUtcNow()));
            }
        }

        foreach (var name in new[] { "Router", "Google", "Cloudflare", "NAS" })
            Feed(name, 5, true);
        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        var before = verdictTransitions.Count;

        Feed("NAS", 30, false);

        Assert.Equal(HostState.Down, monitors[graph.GetByName("NAS").Id].CurrentState);
        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        Assert.Equal(before, verdictTransitions.Count);
    }
}
