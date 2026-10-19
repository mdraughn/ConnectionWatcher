using ConEngine;
using ConEngine.Tracking;
using ConEngine.Tests.Fakes;
using ConInfrastructure.NameResolution;
using ConPersistence.Queries;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConWatch.Tests;

/// <summary>
/// The broadcaster's timing and ordering rules, pinned without a browser or a
/// wall clock: a recording hub, the broadcaster's own FakeTimeProvider (the
/// engine runs on real time so its transitions arrive by themselves; the
/// broadcaster only sees the clock a test advances), and the NAS target, which
/// is Local-scope and so moves nothing else in the engine.
/// </summary>
public class StatusBroadcasterTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-18T12:00:00Z");

    private sealed record Sent(string Method, object? Arg, DateTimeOffset At);

    private sealed class RecordingHub(FakeTimeProvider time) : IHubContext<StatusHub>
    {
        private readonly List<Sent> _log = [];
        private TaskCompletionSource? _holdStatusChanged;

        public IHubClients Clients => new AllOnly(this);
        public IGroupManager Groups => throw new NotSupportedException();

        public IReadOnlyList<Sent> Log { get { lock (_log) return _log.ToArray(); } }
        public int Count(string method) => Log.Count(s => s.Method == method);

        /// <summary>From now on every statusChanged send records itself, then waits to be released.</summary>
        public void HoldStatusChanged() => _holdStatusChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseStatusChanged() => _holdStatusChanged!.SetResult();

        private async Task RecordAsync(string method, object?[] args)
        {
            lock (_log)
                _log.Add(new Sent(method, args.Length > 0 ? args[0] : null, time.GetUtcNow()));
            if (method == "statusChanged" && _holdStatusChanged is { } hold)
                await hold.Task;
        }

        private sealed class AllOnly(RecordingHub hub) : IHubClients
        {
            private IClientProxy Proxy => new Proxy(hub);
            public IClientProxy All => Proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Client(string connectionId) => Proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public IClientProxy Group(string groupName) => Proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
            public IClientProxy User(string userId) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        }

        private sealed class Proxy(RecordingHub hub) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                hub.RecordAsync(method, args);
        }
    }

    private readonly FakeTimeProvider _time = new(T0);
    private readonly RecordingHub _hub;
    private readonly EngineHarness _harness = EngineHarness.Home();
    private readonly TestLedger _ledger;
    private readonly StatusBroadcaster _broadcaster;

    public StatusBroadcasterTests()
    {
        _hub = new RecordingHub(_time);
        _ledger = new TestLedger(_time);
        _broadcaster = new StatusBroadcaster(_hub, _time, NullLogger<StatusBroadcaster>.Instance,
            _ledger.Ledger, new DnsLookupNames());
    }

    public async Task InitializeAsync()
    {
        await _harness.StartAsync();
        await _harness.WaitStateAsync("NAS", HostState.Responding);
        _broadcaster.Attach(_harness.Engine);
    }

    public async Task DisposeAsync()
    {
        _broadcaster.Dispose();
        _ledger.Dispose();
        await _harness.DisposeAsync();
    }

    private bool NasTransitionSeen(string to) => _hub.Log.Any(s =>
        s.Method == "transition" && s.Arg is TransitionDto { SubjectName: "NAS" } d && d.ToState == to);

    /// <summary>Flips the NAS and waits until the broadcaster has been told about the landing state.</summary>
    private async Task NasAsync(bool up)
    {
        _harness.Pings.SetUp(_harness.Ip("NAS"), up);
        var to = up ? "Responding" : "Down";
        var before = _hub.Log.Count(s => s.Method == "transition" && s.Arg is TransitionDto { SubjectName: "NAS" } d && d.ToState == to);
        await TestWait.Eventually(() =>
            _hub.Log.Count(s => s.Method == "transition" && s.Arg is TransitionDto { SubjectName: "NAS" } d && d.ToState == to) > before,
            what: $"the NAS never reported {to} to the hub");
    }

    [Fact]
    public async Task A_transition_goes_out_as_a_transition_message_then_a_snapshot_with_no_clock_advance()
    {
        await NasAsync(up: false);

        // The clock never moved: the first snapshot is owed to the transition, not to the cadence.
        await TestWait.Eventually(() => _hub.Count("statusChanged") >= 1, what: "no snapshot followed the transition");
        var log = _hub.Log.ToList();
        Assert.Equal("transition", log[0].Method);
        var firstSnapshot = log.First(s => s.Method == "statusChanged");
        Assert.True(log.IndexOf(firstSnapshot) > 0, "a snapshot went out before any transition message");
        Assert.All(log, s => Assert.Equal(T0, s.At));
        Assert.IsType<StatusSnapshot>(firstSnapshot.Arg);
        Assert.NotSame(StatusSnapshot.Disabled, firstSnapshot.Arg);
    }

    [Fact]
    public async Task Transitions_inside_the_gap_share_one_snapshot_and_snapshots_are_at_least_250_ms_apart()
    {
        // T0: the NAS goes Down (possibly via Faltering): every transition lands at one fake instant.
        await NasAsync(up: false);
        await TestWait.Eventually(() => _hub.Count("statusChanged") >= 1, what: "no snapshot at T0");
        Assert.Equal(1, _hub.Count("statusChanged"));

        // T0+100 ms: it recovers. More transitions, still inside the gap: no new snapshot.
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await NasAsync(up: true);
        Assert.Equal(1, _hub.Count("statusChanged"));

        // T0+249 ms: one millisecond short of the gap. Still nothing.
        _time.Advance(TimeSpan.FromMilliseconds(149));
        await NasAsync(up: false);
        Assert.Equal(1, _hub.Count("statusChanged"));

        // T0+250 ms: exactly the gap. The next transition earns a snapshot.
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await NasAsync(up: true);
        await TestWait.Eventually(() => _hub.Count("statusChanged") >= 2, what: "no snapshot once the gap had passed");

        var stamps = _hub.Log.Where(s => s.Method == "statusChanged").Select(s => s.At).ToList();
        Assert.Equal(2, stamps.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(250), stamps[1] - stamps[0]);
    }

    [Fact]
    public async Task A_snapshot_queued_behind_an_in_flight_send_is_dropped_once_the_engine_is_detached()
    {
        // One snapshot goes out and is held mid-send, holding the send gate.
        _hub.HoldStatusChanged();
        await NasAsync(up: false);
        await TestWait.Eventually(() => _hub.Count("statusChanged") == 1, what: "the first snapshot never started");

        // A later transition (past the gap) builds its snapshot and queues behind the gate.
        _time.Advance(TimeSpan.FromSeconds(1));
        var transitionsBefore = _hub.Count("transition");
        await NasAsync(up: true);
        // Absence setup, not a result: the transition message is out, so its thread is about to
        // reach the gate; give it the moments it needs. The assertions below are on what was sent.
        await Task.Delay(300);
        Assert.True(_hub.Count("transition") > transitionsBefore);

        // The host archives: detach, then release the wedged send.
        _broadcaster.Detach();
        _hub.ReleaseStatusChanged();
        await _broadcaster.PushDisabledAsync();

        // Exactly one more snapshot reached the wire after the held one: the disabled push.
        // The queued live snapshot must have been dropped by the re-check under the gate.
        var snapshots = _hub.Log.Where(s => s.Method == "statusChanged").ToList();
        Assert.Equal(2, snapshots.Count);
        Assert.NotSame(StatusSnapshot.Disabled, snapshots[0].Arg);
        Assert.Same(StatusSnapshot.Disabled, snapshots[1].Arg);
    }
}
