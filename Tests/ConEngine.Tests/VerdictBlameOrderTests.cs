using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

/// <summary>During weather the state rule blames the shallowest losing path hop
/// (spec 2026-10-09 §4.5, veto removed 2026-10-12; a statistical veto is Plan D2).</summary>
public class VerdictBlameOrderTests : IDisposable
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _topology;
    // Fed to show the verdict ignores it: nothing hands these windows to the verdict.
    private readonly LossWindows _loss = new();
    private readonly VerdictCoordinator _verdict;
    private readonly List<TargetTransition> _transitions = new();

    public VerdictBlameOrderTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal", Operator = "Comcast" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Modem" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        _topology = new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance);
        _topology.Start();
        _verdict = new VerdictCoordinator(_graph, _graph.GetPoolByName("Internet"), _monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance);
        _verdict.TransitionSource.Subscribe(_transitions.Add);
        _verdict.Start();
    }

    public void Dispose()
    {
        _verdict.Dispose();
        _topology.Dispose();
        foreach (var m in _monitors.Values)
            m.Dispose();
    }

    private Guid Id(string name) => _graph.GetByName(name).Id;

    /// <summary>Drives a monitor's state (the state machine), not the windows.</summary>
    private void Feed(string name, int count, bool success)
    {
        var monitor = _monitors[Id(name)];
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    /// <summary>Records one window's samples for the given targets, then closes
    /// it with one sample past the close delay (time moves forward to it).</summary>
    private void CloseWindow(params (string Name, int Samples, int Lost)[] targets)
    {
        var start = LossWindows.WindowStart(_time.GetUtcNow());
        foreach (var (name, samples, lost) in targets)
            for (int i = 0; i < samples; i++)
                _loss.Record(Id(name), start + TimeSpan.FromSeconds(i * 50.0 / samples), success: i >= lost);
        _time.SetUtcNow(start + LossWindows.Span + LossWindows.CloseDelay);
        _loss.Record(Id("Router"), _time.GetUtcNow(), true);
    }

    /// <summary>The modem drops its own pings; Quad9 beyond it loses nothing.</summary>
    private void CloseProbeOnlyWindow() =>
        CloseWindow(("Router", 12, 0), ("Modem", 12, 5), ("Google", 12, 12), ("Cloudflare", 12, 6), ("Quad9", 12, 0));

    /// <summary>The modem and everything beyond it lose about as much.</summary>
    private void CloseRealLossWindow() =>
        CloseWindow(("Router", 12, 0), ("Modem", 12, 4), ("Google", 12, 5), ("Cloudflare", 12, 4), ("Quad9", 12, 4));

    private void Healthy()
    {
        foreach (var name in new[] { "Router", "Modem", "Google", "Cloudflare", "Quad9" })
            Feed(name, 5, true);
    }

    private void Weather()
    {
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false); // two anchors down: weather level (threshold 2)
    }

    private void WeatherWithAFalteringModem()
    {
        Healthy();
        Weather();
        Feed("Modem", 3, false); // and the modem's own probe falters
    }


    [Fact]
    public void A_faltering_modem_during_weather_is_blamed_with_its_operator()
    {
        WeatherWithAFalteringModem();
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Comcast — Modem is Faltering", _verdict.CurrentDetail);
    }

    [Fact]
    public void Loss_starting_at_the_router_blames_the_router_not_the_modem()
    {
        Healthy();
        Weather();
        Feed("Router", 3, false);
        Feed("Modem", 3, false);
        Assert.Equal(HostState.Faltering, _monitors[Id("Router")].CurrentState);
        Assert.Equal(HostState.Faltering, _monitors[Id("Modem")].CurrentState);

        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Router is Faltering", _verdict.CurrentDetail); // the shallowest
    }

    [Fact]
    public void Until_plan_d2_a_faltering_hop_is_blamed_even_if_its_loss_is_only_its_own_probe()
    {
        // Known limitation: the modem drops only its own pings (Quad9 beyond it
        // loses nothing), but the verdict no longer reads loss windows, so the
        // state rule blames it. A statistical veto is planned (Plan D2).
        WeatherWithAFalteringModem();
        CloseProbeOnlyWindow();

        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Comcast — Modem is Faltering", _verdict.CurrentDetail);
    }
}
