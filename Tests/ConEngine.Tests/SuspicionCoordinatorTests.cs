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

public class SuspicionCoordinatorTests
{
    // Analysis window = last 10 samples: two losses in ten stay Responding
    // (0.80), three falter (0.70) — mirrors the default profile's ratios.
    private static readonly PingProfile Fast = new()
    {
        Name = "Fast", HistorySize = 20, MinUsableSample = 10,
        NormalInterval = TimeSpan.FromSeconds(10),
        BurstInterval = TimeSpan.FromSeconds(1),
        BurstMaxDuration = TimeSpan.FromSeconds(60),
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-06T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly SuspicionCoordinator _suspicion;
    private readonly List<GlitchEvent> _glitches = new();

    public SuspicionCoordinatorTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1" },
                new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Modem" },
                new TargetConfig { Name = "Printer", Address = "192.168.1.9", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        _suspicion = new SuspicionCoordinator(_graph, _graph.GetPoolByName("Internet"), _monitors,
            weatherThreshold: 2, _time, NullLogger<SuspicionCoordinator>.Instance);
        _suspicion.GlitchSource.Subscribe(_glitches.Add);
    }

    private TargetMonitor Monitor(string name) => _monitors[_graph.GetByName(name).Id];

    /// <summary>Mirrors the engine's per-result order: discipline, tracker, coordinator.</summary>
    private void Feed(string name, bool success, double advanceSeconds = 1)
    {
        _time.Advance(TimeSpan.FromSeconds(advanceSeconds));
        var monitor = Monitor(name);
        var result = new PingResult(monitor.ResolvedAddress!, success,
            success ? 5 : 0, success ? null : "Timed out", timestamp: _time.GetUtcNow());
        monitor.Discipline.OnPingResult(success);
        monitor.RecordPingResult(result);
        _suspicion.OnPingSample(monitor.Target.Id, result);
    }

    private void PrimeAllResponding()
    {
        foreach (var name in new[] { "Router", "Modem", "Google", "Cloudflare", "Quad9", "Printer" })
            for (int i = 0; i < 10; i++)
                Feed(name, true, advanceSeconds: 0.01);
        Assert.All(_monitors.Values, m => Assert.Equal(HostState.Responding, m.CurrentState));
    }

    [Fact]
    public void Anchor_loss_sweeps_anchors_and_trunk_but_not_bystanders()
    {
        PrimeAllResponding();
        // Priming losses none; ensure politeness guard from any earlier burst can't interfere:
        _time.Advance(TimeSpan.FromSeconds(120));

        Feed("Google", false);

        foreach (var swept in new[] { "Google", "Cloudflare", "Quad9", "Modem", "Router" })
            Assert.True(Monitor(swept).Discipline.IsBurstActive, $"{swept} should be bursting");
        Assert.False(Monitor("Printer").Discipline.IsBurstActive, "Printer is not in the sweep set");
    }

    [Fact]
    public void Bystander_loss_does_not_sweep()
    {
        PrimeAllResponding();
        _time.Advance(TimeSpan.FromSeconds(120));

        Feed("Printer", false);

        foreach (var name in new[] { "Google", "Cloudflare", "Quad9", "Modem", "Router" })
            Assert.False(Monitor(name).Discipline.IsBurstActive, $"{name} must not burst");
    }

    [Fact]
    public void Single_loss_episode_records_no_glitch()
    {
        PrimeAllResponding();
        Feed("Google", false);
        for (int i = 0; i < 5; i++)
            Feed("Google", true);
        Assert.Empty(_glitches);
    }

    [Fact]
    public void Two_loss_episode_records_glitch_with_span_and_counts()
    {
        PrimeAllResponding();
        _time.Advance(TimeSpan.FromSeconds(120));

        Feed("Google", false);
        var firstLoss = _time.GetUtcNow();
        Feed("Google", true);
        Feed("Google", false);
        var lastLoss = _time.GetUtcNow();
        for (int i = 0; i < 5; i++)
            Feed("Google", true);

        var glitch = Assert.Single(_glitches);
        Assert.Equal(GlitchClass.Target, glitch.Class);
        Assert.Equal("Google", glitch.TargetName);
        Assert.Equal(firstLoss, glitch.StartUtc);
        Assert.Equal(lastLoss, glitch.EndUtc);
        Assert.Equal(2, glitch.LostCount);
        Assert.Equal(8, glitch.SampleCount); // 2 losses + 1 + 5 successes
        Assert.False(glitch.Coalesced);
    }

    [Fact]
    public void Faltering_escalation_discards_the_episode()
    {
        PrimeAllResponding();
        Feed("Google", false);
        Feed("Google", false);
        Feed("Google", false); // 7/10 -> Faltering: transitions own it now
        Assert.Equal(HostState.Faltering, Monitor("Google").CurrentState);
        for (int i = 0; i < 10; i++)
            Feed("Google", true);
        Assert.Empty(_glitches);
    }

    [Fact]
    public void Adjacent_episodes_coalesce()
    {
        PrimeAllResponding();
        _time.Advance(TimeSpan.FromSeconds(120));

        Feed("Google", false);
        Feed("Google", false);
        for (int i = 0; i < 5; i++)
            Feed("Google", true); // first glitch emitted
        for (int i = 0; i < 5; i++)
            Feed("Google", true); // age the losses out of the 10-sample window

        _time.Advance(TimeSpan.FromSeconds(20)); // still inside the 60s coalesce gap
        Feed("Google", false);
        Feed("Google", false);
        for (int i = 0; i < 5; i++)
            Feed("Google", true); // second glitch: coalesced

        Assert.Equal(2, _glitches.Count(g => g.Class == GlitchClass.Target));
        Assert.False(_glitches[0].Coalesced);
        Assert.True(_glitches[^1].Coalesced);
    }

    [Fact]
    public void Correlated_anchor_glitches_emit_an_internet_glitch()
    {
        PrimeAllResponding();
        _time.Advance(TimeSpan.FromSeconds(120));

        // Google and Cloudflare glitch in the same few seconds; Quad9 clean.
        Feed("Google", false);
        Feed("Cloudflare", false);
        Feed("Google", false);
        Feed("Cloudflare", false);
        for (int i = 0; i < 5; i++)
        {
            Feed("Google", true);
            Feed("Cloudflare", true);
        }

        var internet = Assert.Single(_glitches, g => g.Class == GlitchClass.Internet);
        Assert.Equal(VerdictCoordinator.SubjectId, internet.TargetId);
        Assert.Contains("Google", internet.Detail);
        Assert.Contains("Cloudflare", internet.Detail);
        Assert.Contains("upstream", internet.Detail); // trunk stayed clean
        Assert.Equal(2, _glitches.Count(g => g.Class == GlitchClass.Target));
    }

    [Fact]
    public void Trunk_involvement_changes_the_wording()
    {
        PrimeAllResponding();
        _time.Advance(TimeSpan.FromSeconds(120));

        Feed("Modem", false);
        Feed("Google", false);
        Feed("Cloudflare", false);
        Feed("Modem", false);
        Feed("Google", false);
        Feed("Cloudflare", false);
        for (int i = 0; i < 5; i++)
        {
            Feed("Modem", true);
            Feed("Google", true);
            Feed("Cloudflare", true);
        }

        var internet = Assert.Single(_glitches, g => g.Class == GlitchClass.Internet);
        Assert.Contains("link/ISP", internet.Detail);
    }

    [Fact]
    public void Single_anchor_glitch_stays_a_target_glitch()
    {
        PrimeAllResponding();
        _time.Advance(TimeSpan.FromSeconds(120));

        Feed("Google", false);
        Feed("Google", false);
        for (int i = 0; i < 5; i++)
            Feed("Google", true);

        Assert.Single(_glitches);
        Assert.DoesNotContain(_glitches, g => g.Class == GlitchClass.Internet);
    }
}
