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

/// <summary>Spec 2026-10-09 §4.5, revised 2026-10-12: during weather the state
/// rule blames the shallowest losing path hop, unless the recent windows show
/// its loss is its own probe only (the veto).</summary>
public class VerdictLossAttributionTests : IDisposable
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _topology;
    private readonly LossWindows _loss = new();
    private readonly VerdictCoordinator _verdict;
    private readonly List<TargetTransition> _transitions = new();

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
    public void Without_windows_the_state_rule_blames_a_faltering_hop()
    {
        WeatherWithAFalteringModem();
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Comcast — Modem is Faltering", _verdict.CurrentDetail);
    }

    [Fact]
    public void A_modem_dropping_only_its_own_pings_is_vetoed()
    {
        WeatherWithAFalteringModem();
        CloseProbeOnlyWindow();

        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
    }

    [Fact]
    public void Real_loss_from_the_modem_onward_is_link_degraded()
    {
        WeatherWithAFalteringModem();
        CloseRealLossWindow();

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

        CloseWindow(("Router", 12, 4), ("Modem", 12, 4), ("Google", 12, 5), ("Cloudflare", 12, 4), ("Quad9", 12, 4));

        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Router is Faltering", _verdict.CurrentDetail); // the shallowest
    }

    [Fact]
    public void A_veto_lifts_only_at_ten_percent_pooled_forwarding_loss()
    {
        WeatherWithAFalteringModem();
        CloseProbeOnlyWindow(); // forwarding 0: vetoed
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);

        // Pooled forwarding climbs to 3 of 42 (7 %): above the 5 % veto line,
        // below the 10 % lift line, so the veto holds.
        for (int i = 0; i < 3; i++)
        {
            CloseWindow(("Router", 14, 0), ("Modem", 14, 6), ("Google", 14, 3), ("Cloudflare", 14, 3), ("Quad9", 14, 1));
            Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
        }

        // Pooled forwarding reaches 6 of 40 (15 %): the veto lifts.
        CloseWindow(("Router", 12, 0), ("Modem", 12, 6), ("Google", 12, 4), ("Cloudflare", 12, 4), ("Quad9", 12, 4));
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Comcast — Modem is Faltering", _verdict.CurrentDetail);
    }

    [Fact]
    public void Noisy_real_loss_at_the_modem_blames_it_once_and_holds()
    {
        WeatherWithAFalteringModem();
        var before = _transitions.Count;

        for (int i = 0; i < 15; i++)
        {
            // Every target loses 3 to 5 of 12, varied so the window minimum and
            // the anchors' median rarely coincide.
            CloseWindow(("Router", 12, 0), ("Modem", 12, 3 + i % 3), ("Google", 12, 3 + (i + 1) % 3),
                ("Cloudflare", 12, 3 + (i + 2) % 3), ("Quad9", 12, 3 + (2 * i + 1) % 3));
            Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        }

        Assert.Single(_transitions, t => t.Reason.Verdict == nameof(VerdictState.LinkDegraded));
        Assert.Equal(before, _transitions.Count); // no flips while the windows close
    }

    [Fact]
    public void Noisy_probe_only_loss_at_the_modem_never_leaves_weather_once_vetoed()
    {
        WeatherWithAFalteringModem();

        for (int i = 0; i < 15; i++)
        {
            // The modem loses 2 to 6 of 12 of its own pings; Quad9 0 or 1 (pooled at most 2 of 36).
            CloseWindow(("Router", 12, 0), ("Modem", 12, 2 + i * 3 % 5), ("Google", 12, 12),
                ("Cloudflare", 12, 6), ("Quad9", 12, i % 3 == 0 ? 0 : 1));
            Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
        }
    }

    [Fact]
    public void Stale_windows_do_not_veto()
    {
        Healthy();
        CloseProbeOnlyWindow();
        _time.Advance(LossWindows.Span * 4); // the window is now older than four spans
        Weather();
        Feed("Modem", 3, false);
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict); // the state rule: faltering hop
    }

    [Fact]
    public void A_closing_window_reclassifies_without_any_state_change()
    {
        WeatherWithAFalteringModem();
        CloseProbeOnlyWindow();
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);

        var states = _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);
        CloseRealLossWindow(); // pooled forwarding 4 of 24 (17 %): the veto lifts

        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal(states, _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState)); // windows never change a state
    }
}
