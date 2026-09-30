using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class EngineExtensionTests
{
    [Fact]
    public void TargetGraph_preserves_provided_ids()
    {
        var routerId = Guid.NewGuid();
        var g = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets = [new TargetConfig { Id = routerId, Name = "Router", Address = "10.0.0.1", Profile = "Internal" }],
        });
        Assert.Equal(routerId, g.GetByName("Router").Id);
    }

    [Fact]
    public void TargetGraph_preserves_provided_pool_ids()
    {
        var poolId = Guid.NewGuid();
        var g = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets = [new TargetConfig { Name = "Google", Address = "8.8.8.8" }],
            Pools = [new PoolConfig { Id = poolId, Name = "Internet", Members = ["Google"] }],
        });
        Assert.Equal(poolId, g.GetPoolByName("Internet").Id);
    }

    [Fact]
    public async Task Engine_emits_ping_samples_with_target_ids()
    {
        var options = new WatcherOptions
        {
            Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
            Targets = [new TargetConfig { Name = "Router", Address = "10.0.0.9", Profile = "Fast" }],
        };
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        var samples = new List<(Guid, ConCore.PingResult)>();
        engine.PingSampleSource.Subscribe(s => { lock (samples) samples.Add(s); });
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            lock (samples) { if (samples.Count >= 3) break; }
            await Task.Delay(20);
        }
        cts.Cancel();
        await engine.StopAsync();
        lock (samples)
        {
            Assert.True(samples.Count >= 3);
            Assert.All(samples, s => Assert.Equal(engine.Graph.GetByName("Router").Id, s.Item1));
        }
    }

    [Fact]
    public void Transitions_default_to_target_kind()
    {
        var t = new TargetTransition
        {
            TargetId = Guid.NewGuid(), TargetName = "x", From = HostState.Initializing,
            To = HostState.Starting, Timestamp = DateTimeOffset.UtcNow,
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed },
        };
        Assert.Equal(TransitionSubjectKind.Target, t.Kind);
    }
}
