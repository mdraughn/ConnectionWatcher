using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class PoolCoordinatorTests
{
    private static readonly PingProfile Profile = new()
    {
        Name = "Test", HistorySize = 20, MinUsableSample = 5,
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
    private readonly TargetMonitor _google;
    private readonly TargetMonitor _cloudflare;
    private readonly PoolCoordinator _pool;
    private readonly List<TargetTransition> _poolTransitions = new();

    public PoolCoordinatorTests()
    {
        _google = MakeMonitor("Google", "8.8.8.8");
        _cloudflare = MakeMonitor("Cloudflare", "1.1.1.1");
        var def = new PoolDefinition
        {
            Id = Guid.NewGuid(), Name = "Internet",
            MemberTargetIds = [_google.Target.Id, _cloudflare.Target.Id],
        };
        _pool = new PoolCoordinator(def, [_google, _cloudflare], _time,
            NullLogger<PoolCoordinator>.Instance);
        _pool.TransitionSource.Subscribe(_poolTransitions.Add);
        _pool.Start();
    }

    private TargetMonitor MakeMonitor(string name, string ip)
    {
        var target = new Target { Id = Guid.NewGuid(), Name = name, Address = ip, ProfileName = "Test" };
        var monitor = new TargetMonitor(target, Profile, new PingDiscipline(Profile, _time),
            _time, NullLoggerFactory.Instance);
        monitor.StartTracking(IPAddress.Parse(ip));
        return monitor;
    }

    private void Feed(TargetMonitor m, int count, bool success)
    {
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            m.RecordPingResult(new PingResult(m.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    [Fact]
    public void Dispose_twice_completes_the_stream_once()
    {
        var completions = 0;
        _pool.TransitionSource.Subscribe(_ => { }, () => completions++);
        _pool.Dispose();
        _pool.Dispose();
        Assert.Equal(1, completions);
    }

    [Fact]
    public void Phase_offsets_stagger_members()
    {
        var interval = TimeSpan.FromSeconds(15);
        Assert.Equal(TimeSpan.Zero, PoolCoordinator.PhaseOffset(0, 3, interval));
        Assert.Equal(TimeSpan.FromSeconds(5), PoolCoordinator.PhaseOffset(1, 3, interval));
        Assert.Equal(TimeSpan.FromSeconds(10), PoolCoordinator.PhaseOffset(2, 3, interval));
    }

    [Fact]
    public void Pool_responds_when_any_member_responds()
    {
        Feed(_google, 5, true);
        Assert.Equal(HostState.Responding, _pool.CurrentState);
    }

    [Fact]
    public void Pool_rides_through_single_member_outage()
    {
        Feed(_google, 5, true);
        Feed(_cloudflare, 5, true);
        Feed(_google, 20, false); // Google: Responding -> ... -> Down
        Assert.Equal(HostState.Down, _google.CurrentState);
        Assert.Equal(HostState.Responding, _pool.CurrentState);
    }

    [Fact]
    public void Pool_down_when_all_members_down_with_detail()
    {
        Feed(_google, 5, true);
        Feed(_cloudflare, 5, true);
        Feed(_google, 20, false);
        Feed(_cloudflare, 20, false);

        Assert.Equal(HostState.Down, _pool.CurrentState);
        var t = _poolTransitions.Last();
        Assert.Equal(TransitionTrigger.PoolMembersChanged, t.Reason.Trigger);
        Assert.Equal(TransitionSubjectKind.Pool, t.Kind);
        Assert.Contains("Google=Down", t.Reason.Detail);
    }

    [Fact]
    public void Pool_faltering_when_members_degraded_but_not_all_down()
    {
        Feed(_google, 5, true);
        Feed(_cloudflare, 5, true);
        Feed(_google, 20, false);                 // Google Down
        Feed(_cloudflare, 3, false);              // 2 of last 5 ok (40%): Faltering, not Down
        Assert.Equal(HostState.Faltering, _cloudflare.CurrentState);
        Assert.Equal(HostState.Faltering, _pool.CurrentState);
    }

    [Fact]
    public void Pool_stays_starting_until_a_member_resolves()
    {
        Assert.Equal(HostState.Initializing, _pool.CurrentState);
        Feed(_google, 2, true); // not enough for a usable sample yet
        Assert.True(_pool.CurrentState is HostState.Initializing or HostState.Starting);
    }

    [Fact]
    public void Pool_transitions_are_published_outside_the_pool_lock()
    {
        // A subscriber that needs the pool's lock from ANOTHER thread must not
        // wait for the publisher. Seed takes the lock (and is a no-op once the
        // pool has left Initializing).
        var results = new List<bool>();
        using var subscription = _pool.TransitionSource.Subscribe(_ =>
            results.Add(Task.Run(() => _pool.Seed(HostState.Down)).Wait(TimeSpan.FromSeconds(10))));

        Feed(_google, 5, success: true);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r));
    }
}
