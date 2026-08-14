using System.Net;
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
    public async Task Healthy_network_reaches_responding_and_pool_responds()
    {
        await Eventually(() => State("Router") == HostState.Responding);
        await Eventually(() => State("Google") == HostState.Responding);
        await Eventually(() => _engine.PoolCoordinators.Single().CurrentState == HostState.Responding);
    }

    [Fact]
    public async Task Router_down_cascades_and_pinging_pauses_for_descendants()
    {
        await Eventually(() => State("Google") == HostState.Responding);

        _factory.SetUp(RouterIp, false);
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

        // Recovery: router back up -> descendants released and re-prove.
        _factory.SetUp(RouterIp, true);
        await Eventually(() => State("Router") == HostState.Responding);
        await Eventually(() => State("Google") == HostState.Responding);
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
