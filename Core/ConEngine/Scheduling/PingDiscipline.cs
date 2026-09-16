using ConCore.Targets;
using ConEngine.Tracking;

namespace ConEngine.Scheduling;

/// <summary>
/// Adaptive per-target ping cadence: normal interval when healthy, burst interval
/// (time-capped) while Faltering/Recovering/Down, parked while Unreachable.
/// A lost ping (or an external TriggerBurst from the corroboration sweep) also
/// starts a "suspicion burst" while still Responding, guarded so a chronically
/// lossy line cannot keep the burst alive forever. Normal-cadence pings are
/// phase-locked to slots at epoch + phaseOffset + k·interval, so pool members
/// staggered across the interval return to their stagger after any burst.
/// </summary>
public class PingDiscipline : IPingCadence
{
    private static readonly TimeSpan SuspicionGuard = TimeSpan.FromSeconds(60);

    private readonly PingProfile _profile;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly DateTimeOffset _epoch;
    private readonly TimeSpan _phaseOffset;
    private DateTimeOffset? _nextNormalDue;
    private HostState _state = HostState.Initializing;
    private DateTimeOffset? _burstStartedAt;
    private DateTimeOffset? _lastBurstStart;
    private TaskCompletionSource _releaseTcs = NewTcs();

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PingDiscipline(PingProfile profile, TimeProvider timeProvider, TimeSpan phaseOffset = default)
    {
        _profile = profile;
        _time = timeProvider;
        _epoch = timeProvider.GetUtcNow();
        _phaseOffset = phaseOffset;
    }

    /// <summary>Start of the active burst window, or null when not bursting.</summary>
    public DateTimeOffset? BurstStartedAt
    {
        get { lock (_lock) { return _burstStartedAt; } }
    }

    /// <summary>True while the target is Unreachable and pinging is suspended.</summary>
    public bool IsParked
    {
        get { lock (_lock) { return _state == HostState.Unreachable; } }
    }

    /// <summary>Feed state transitions in; the cadence adapts.</summary>
    public void OnStateChanged(HostState newState)
    {
        lock (_lock)
        {
            var oldState = _state;
            _state = newState;

            if (newState is HostState.Faltering or HostState.Recovering or HostState.Down)
            {
                // State changes always (re)start the burst window: they are
                // rate-limited by the analysis window itself, not the guard.
                _burstStartedAt = _time.GetUtcNow();
                _lastBurstStart = _burstStartedAt;
            }
            else
            {
                _burstStartedAt = null;
            }

            if (oldState == HostState.Unreachable && newState != HostState.Unreachable)
                _releaseTcs.TrySetResult(); // unpark any waiter
            if (newState == HostState.Unreachable && oldState != HostState.Unreachable)
            {
                _releaseTcs = NewTcs();
                _nextNormalDue = null; // the grid re-anchors after the park
            }
        }
    }

    /// <summary>True while a burst window is active (any state).</summary>
    public bool IsBurstActive
    {
        get
        {
            lock (_lock)
            {
                return _burstStartedAt is { } start &&
                       _time.GetUtcNow() - start < _profile.BurstMaxDuration;
            }
        }
    }

    /// <summary>A lost ping while healthy starts a suspicion burst; successes never do.</summary>
    public void OnPingResult(bool success)
    {
        if (success)
            return;
        lock (_lock)
        {
            if (_state is not (HostState.Responding or HostState.Starting))
                return;
            StartSuspicionBurstLocked();
        }
    }

    /// <summary>Commanded by the corroboration sweep: burst without a state change.</summary>
    public void TriggerBurst()
    {
        lock (_lock)
        {
            if (_state == HostState.Unreachable)
                return;
            StartSuspicionBurstLocked();
        }
    }

    private void StartSuspicionBurstLocked()
    {
        var now = _time.GetUtcNow();
        if (_burstStartedAt is { } active && now - active < _profile.BurstMaxDuration)
            return; // already bursting; never extend
        if (_lastBurstStart is { } last && now - last < SuspicionGuard)
            return; // politeness: a lossy line must not chain bursts forever
        _burstStartedAt = now;
        _lastBurstStart = now;
    }

    public async Task WaitForNextPingAsync(CancellationToken ct)
    {
        while (true)
        {
            Task? parkTask = null;
            TimeSpan delay = TimeSpan.Zero;
            lock (_lock)
            {
                if (_state == HostState.Unreachable)
                {
                    parkTask = _releaseTcs.Task;
                }
                else
                {
                    delay = ComputeDelay();
                }
            }

            if (parkTask is not null)
            {
                await parkTask.WaitAsync(ct).ConfigureAwait(false);
                continue; // re-evaluate now that we're released
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);

            lock (_lock)
            {
                if (_state != HostState.Unreachable)
                    return;
                // Went unreachable while we were waiting: loop around and park.
            }
        }
    }

    private TimeSpan ComputeDelay()
    {
        var now = _time.GetUtcNow();
        // Any active burst window bursts — including a suspicion burst while
        // the state is still Responding.
        if (_burstStartedAt is { } start && now - start < _profile.BurstMaxDuration)
        {
            _nextNormalDue = null; // the grid re-anchors after the burst
            return _profile.BurstInterval;
        }
        // Normal cadence is PHASE-LOCKED: aim at the next slot on this
        // member's own grid (epoch + offset + k·interval) instead of
        // "interval from now". A flat interval lets one lockstep burst (a
        // corroboration sweep, the warm-start confirm) collapse a pool's
        // stagger permanently; slots make it self-healing. Lateness must
        // never cost sample rate, though — under load a wakeup can land past
        // its due slot (or several, at millisecond test cadences): the owed
        // ping fires IMMEDIATELY and the grid resumes at the next slot.
        var interval = _profile.NormalInterval;
        var due = _nextNormalDue ?? NextSlotAfter(now, interval);
        if (due <= now)
        {
            _nextNormalDue = NextSlotAfter(now, interval);
            return TimeSpan.Zero;
        }
        _nextNormalDue = due + interval;
        return due - now;
    }

    /// <summary>The first grid point strictly after <paramref name="now"/>.</summary>
    private DateTimeOffset NextSlotAfter(DateTimeOffset now, TimeSpan interval)
    {
        var ahead = now - _epoch - _phaseOffset;
        if (ahead < TimeSpan.Zero)
            return now - ahead; // before the first slot: the classic initial delay
        var rem = ahead.Ticks % interval.Ticks;
        return now + TimeSpan.FromTicks(interval.Ticks - rem);
    }
}
