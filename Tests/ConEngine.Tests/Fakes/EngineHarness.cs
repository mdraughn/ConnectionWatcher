using System.Net;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ConEngine.Tests.Fakes;

/// <summary>The lists a test may edit before the harness starts.</summary>
public sealed class HomeOptions
{
    public Dictionary<string, PingProfileConfig> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TargetConfig> Targets { get; } = new();
    public List<PoolConfig> Pools { get; } = new();
    public VerdictOptions Verdict { get; } = new();
}

/// <summary>A started-on-demand engine over fake pings and a fixed home topology,
/// recording transitions and glitches. Later engine tests build on it.</summary>
public sealed class EngineHarness : IAsyncDisposable
{
    private readonly WatcherOptions _options;
    private readonly VerdictOptions _verdict;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = new();
    private WatcherEngine? _engine;
    private bool _started;

    private EngineHarness(WatcherOptions options, VerdictOptions verdict)
    {
        _options = options;
        _verdict = verdict;
    }

    /// <summary>Router, Modem (Comcast), pooled Google and Cloudflare, Quad9, Printer, NAS, Resolver.</summary>
    public static EngineHarness Home(Action<HomeOptions>? tweak = null)
    {
        var home = new HomeOptions();
        home.Profiles["Fast"] = new PingProfileConfig
        {
            NormalIntervalSeconds = 0.02,
            BurstIntervalSeconds = 0.01,
            BurstMaxDurationSeconds = 0.2,
            PingTimeoutSeconds = 0.5,
            HistorySize = 10,
            MinUsableSample = 5,
        };
        home.Targets.AddRange(
        [
            new TargetConfig { Name = "Router", Address = "192.0.2.1", Profile = "Fast" },
            new TargetConfig { Name = "Modem", Address = "192.0.2.2", Parent = "Router", Profile = "Fast", Operator = "Comcast" },
            new TargetConfig { Name = "Google", Address = "198.51.100.8", Parent = "Modem", Profile = "Fast" },
            new TargetConfig { Name = "Cloudflare", Address = "198.51.100.1", Parent = "Modem", Profile = "Fast" },
            new TargetConfig { Name = "Quad9", Address = "198.51.100.9", Parent = "Modem", Profile = "Fast" },
            new TargetConfig { Name = "Printer", Address = "192.0.2.50", Profile = "Fast", Scope = TargetScope.Verdict },
            new TargetConfig { Name = "NAS", Address = "192.0.2.60", Profile = "Fast", Scope = TargetScope.Local },
            new TargetConfig
            {
                Name = "Resolver", Address = "192.0.2.53", Profile = "Fast", Probe = "dns",
                Scope = TargetScope.Resolver, SystemManaged = true,
            },
        ]);
        home.Pools.Add(new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] });
        home.Verdict.AnchorPool = "Internet";
        home.Verdict.WeatherThreshold = 2;
        home.Verdict.DnsWatch = true;
        tweak?.Invoke(home);

        var options = new WatcherOptions { Targets = home.Targets, Pools = home.Pools };
        foreach (var (name, profile) in home.Profiles)
            options.Profiles[name] = profile;
        return new EngineHarness(options, home.Verdict);
    }

    public FakePingTransceiverFactory Pings { get; } = new();

    public TimeProvider Time { get; private set; } = TimeProvider.System;

    private readonly List<TargetTransition> _transitions = new();
    private readonly List<GlitchEvent> _glitches = new();

    /// <summary>Every transition seen so far; each read is a snapshot copy taken under the lock.</summary>
    public IReadOnlyList<TargetTransition> Transitions { get { lock (_gate) return _transitions.ToArray(); } }

    /// <summary>Every glitch seen so far; each read is a snapshot copy taken under the lock.</summary>
    public IReadOnlyList<GlitchEvent> Glitches { get { lock (_gate) return _glitches.ToArray(); } }

    /// <summary>Available once <see cref="StartAsync"/> has been called.</summary>
    public WatcherEngine Engine => _engine ?? throw new InvalidOperationException("Harness not started");

    /// <summary>Swaps in a timer-counting fake clock; only before start.</summary>
    public TimerCountingTime UseFakeTime()
    {
        if (_started)
            throw new InvalidOperationException("UseFakeTime must be called before StartAsync");
        var fake = new TimerCountingTime();
        Time = fake;
        return fake;
    }

    public Guid Id(string name) => Engine.Graph.GetByName(name).Id;

    public IPAddress Ip(string name) =>
        IPAddress.Parse(_options.Targets.Single(t => t.Name == name).Address);

    public async Task StartAsync(EngineSeed? seed = null)
    {
        _started = true;
        _engine = new WatcherEngine(Time, Pings, new ConInfrastructure.NameResolution.DomainNameResolverFactory(),
            NullLoggerFactory.Instance, Options.Create(_options), Options.Create(_verdict));
        _subscriptions.Add(_engine.TransitionSource.Subscribe(t => { lock (_gate) _transitions.Add(t); }));
        _subscriptions.Add(_engine.GlitchSource.Subscribe(g => { lock (_gate) _glitches.Add(g); }));
        await _engine.StartAsync(_cts.Token, seed);
    }

    public Task WaitStateAsync(string name, HostState state) =>
        TestWait.Eventually(() => Engine.Monitors[Id(name)].CurrentState == state,
            what: $"{name} never reached {state}");

    public Task WaitVerdictAsync(VerdictState state) =>
        TestWait.Eventually(() => Engine.Verdict?.CurrentVerdict == state,
            what: $"the verdict never reached {state}");

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_engine is not null)
            await _engine.StopAsync();
        foreach (var s in _subscriptions)
            s.Dispose();
        _cts.Dispose();
    }
}
