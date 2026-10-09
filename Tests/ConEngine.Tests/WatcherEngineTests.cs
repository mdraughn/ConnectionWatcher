using System.Net;
using ConCore;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class WatcherEngineTests : IAsyncLifetime
{
    private static readonly IPAddress RouterIp = IPAddress.Parse("192.168.1.1");
    private static readonly IPAddress GoogleIp = IPAddress.Parse("8.8.8.8");

    private readonly FakePingTransceiverFactory _factory = new();
    private readonly CancellationTokenSource _cts = new();
    private WatcherEngine _engine = null!;
    private readonly List<TargetTransition> _transitions = new();

    private static WatcherOptions Options() => new()
    {
        Profiles =
        {
            ["Fast"] = new PingProfileConfig
            {
                NormalIntervalSeconds = 0.02,
                BurstIntervalSeconds = 0.01,
                BurstMaxDurationSeconds = 5,
                HistorySize = 20,
                MinUsableSample = 5,
            },
        },
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Fast" },
            new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router", Profile = "Fast" },
            new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router", Profile = "Fast" },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
    };

    public async Task InitializeAsync()
    {
        _engine = new WatcherEngine(TimeProvider.System, _factory,
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        _engine.TransitionSource.Subscribe(t => { lock (_transitions) { _transitions.Add(t); } });
        await _engine.StartAsync(_cts.Token);
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        await _engine.StopAsync();
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail("Condition not met within timeout");
    }

    private HostState State(string name) => _engine.Monitors[_engine.Graph.GetByName(name).Id].CurrentState;

    [Fact]
    public async Task Two_targets_on_one_ip_are_tracked_independently()
    {
        var sharedIp = IPAddress.Parse("192.168.9.1");
        var options = new WatcherOptions
        {
            Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
            Targets =
            [
                new TargetConfig { Name = "RouterIcmp", Address = "192.168.9.1", Profile = "Fast" },
                new TargetConfig { Name = "RouterHttps", Address = "192.168.9.1", Profile = "Fast", Probe = "tcp:443" },
            ],
        };
        var fake = new FakePingTransceiverFactory();
        var engine = new WatcherEngine(TimeProvider.System, fake,
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var icmp = engine.Monitors[engine.Graph.GetByName("RouterIcmp").Id];
            var tcp = engine.Monitors[engine.Graph.GetByName("RouterHttps").Id];
            await Eventually(() => icmp.CurrentState == HostState.Responding && tcp.CurrentState == HostState.Responding);
            await Eventually(() => icmp.PingCount >= 5 && tcp.PingCount >= 5);

            var probes = fake.Created.Where(c => c.Ip.Equals(sharedIp)).Select(c => c.Probe).ToList();
            Assert.Contains(ProbeSpec.Icmp, probes);
            Assert.Contains(new ProbeSpec(ProbeKind.Tcp, 443), probes);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Healthy_network_reaches_responding_and_pool_responds()
    {
        await Eventually(() => State("Router") == HostState.Responding);
        await Eventually(() => State("Google") == HostState.Responding);
        await Eventually(() => _engine.PoolCoordinators.Single().CurrentState == HostState.Responding);
    }

    [Fact]
    public async Task Router_down_with_everything_beyond_down_is_explained_by_the_router()
    {
        await Eventually(() => State("Google") == HostState.Responding);

        _factory.SetUp(RouterIp, false);
        _factory.SetUp(GoogleIp, false);
        _factory.SetUp(IPAddress.Parse("1.1.1.1"), false);
        await Eventually(() => State("Router") == HostState.Down);
        await Eventually(() => State("Google") == HostState.Unreachable);
        await Eventually(() => State("Cloudflare") == HostState.Unreachable);

        bool sawParentDown;
        lock (_transitions)
        {
            sawParentDown = _transitions.Any(t =>
                t.TargetName == "Google" && t.Reason.Trigger == TransitionTrigger.ParentDown);
        }
        Assert.True(sawParentDown);

        // Recovery: everything back -> descendants released and re-prove.
        _factory.SetUp(RouterIp, true);
        _factory.SetUp(GoogleIp, true);
        _factory.SetUp(IPAddress.Parse("1.1.1.1"), true);
        await Eventually(() => State("Router") == HostState.Responding);
        await Eventually(() => State("Google") == HostState.Responding);
    }

    [Fact]
    public async Task Router_down_while_everything_beyond_answers_parks_nothing()
    {
        await Eventually(() => State("Google") == HostState.Responding);

        _factory.SetUp(RouterIp, false); // only the router's own probe fails
        await Eventually(() => State("Router") == HostState.Down);

        // Wait for enough fresh probes beyond it to have been judged.
        var google = _engine.Monitors.Values.Single(m => m.Target.Name == "Google");
        var after = google.PingCount;
        await Eventually(() => google.PingCount >= after + 10);
        Assert.Equal(HostState.Responding, State("Google"));
        Assert.Equal(HostState.Responding, State("Cloudflare"));
    }

    [Fact]
    public async Task Single_external_outage_marks_down_but_pool_survives()
    {
        await Eventually(() => State("Google") == HostState.Responding);
        await Eventually(() => State("Cloudflare") == HostState.Responding);

        _factory.SetUp(GoogleIp, false);
        await Eventually(() => State("Google") == HostState.Down);
        Assert.Equal(HostState.Responding, State("Cloudflare"));
        Assert.Equal(HostState.Responding, _engine.PoolCoordinators.Single().CurrentState);

        bool downHasEvidence;
        lock (_transitions)
        {
            var t = _transitions.Last(x => x.TargetName == "Google" && x.To == HostState.Down);
            downHasEvidence = t.Reason.SuccessRate is not null && t.Reason.SampleSize is not null;
        }
        Assert.True(downHasEvidence);
    }
}
