using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-09 §4.5: LinkDegraded only when the loss is our path's.</summary>
public class VerdictLossAttributionTests : IDisposable
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _topology;
    private readonly LossWindows _loss = new();
    private readonly VerdictCoordinator _verdict;

    public VerdictLossAttributionTests()
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
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance, _loss);
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

    private void Healthy()
    {
        foreach (var name in new[] { "Router", "Modem", "Google", "Cloudflare", "Quad9" })
            Feed(name, 5, true);
    }

    private void WeatherWithAFalteringModem()
    {
        Healthy();
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false); // two anchors down: weather level (threshold 2)
        Feed("Modem", 3, false);       // and the modem's own probe falters
    }

    [Fact]
    public void Without_a_window_the_interim_rule_still_blames_a_faltering_hop()
    {
        WeatherWithAFalteringModem();
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
    }

    [Fact]
    public void A_modem_dropping_only_its_own_pings_is_not_blamed_for_weather()
    {
        // Review Focus 1: Quad9 beyond the modem lost nothing, so the modem forwarded everything.
        WeatherWithAFalteringModem();
        CloseWindow(("Router", 12, 0), ("Modem", 12, 5), ("Google", 12, 12), ("Cloudflare", 12, 6), ("Quad9", 12, 0));

        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
    }

    [Fact]
    public void Real_loss_from_the_modem_onward_is_link_degraded_with_its_forwarding_loss()
    {
        // Review Focus 2.
        WeatherWithAFalteringModem();
        CloseWindow(("Router", 12, 0), ("Modem", 12, 4), ("Google", 12, 5), ("Cloudflare", 12, 4), ("Quad9", 12, 4));

        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Comcast — Modem is Faltering (forwarding loss 33 %)", _verdict.CurrentDetail);
    }

    [Fact]
    public void Loss_below_the_anchors_typical_loss_is_not_the_paths()
    {
        WeatherWithAFalteringModem();
        // Modem forwards 1 in 12 lost (8 %); the anchors typically lose 50 %.
        CloseWindow(("Router", 12, 0), ("Modem", 12, 1), ("Google", 12, 6), ("Cloudflare", 12, 6), ("Quad9", 12, 1));
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
    }

    [Fact]
    public void A_closing_window_reclassifies_without_any_state_change()
    {
        WeatherWithAFalteringModem();
        CloseWindow(("Router", 12, 0), ("Modem", 12, 5), ("Google", 12, 12), ("Cloudflare", 12, 6), ("Quad9", 12, 0));
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);

        var states = _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);
        CloseWindow(("Router", 12, 0), ("Modem", 12, 4), ("Google", 12, 5), ("Cloudflare", 12, 4), ("Quad9", 12, 4));

        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal(states, _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState)); // attribution never changes a state
    }

    [Fact]
    public void A_stale_window_falls_back_to_the_interim_rule()
    {
        Healthy();
        CloseWindow(("Router", 12, 0), ("Modem", 12, 5), ("Google", 12, 0), ("Cloudflare", 12, 0), ("Quad9", 12, 0));
        _time.Advance(LossWindows.Span * 3); // the window is now stale
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);
        Feed("Modem", 3, false);
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict); // interim: faltering hop
    }
}
