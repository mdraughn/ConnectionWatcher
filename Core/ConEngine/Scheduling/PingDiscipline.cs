using ConCore.Targets;
using ConEngine.Tracking;

namespace ConEngine.Scheduling;

/// <summary>
/// Adaptive per-target ping cadence: normal interval when healthy, burst interval
/// (time-capped) while Faltering/Recovering/Down, parked while Unreachable.
/// </summary>
public class PingDiscipline : IPingCadence
{
    private readonly PingProfile _profile;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private HostState _state = HostState.Initializing;
    private DateTimeOffset? _burstStartedAt;
    private TimeSpan? _initialDelay;
    private TaskCompletionSource _releaseTcs = NewTcs();

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PingDiscipline(PingProfile profile, TimeProvider timeProvider, TimeSpan initialDelay = default)
    {
        _profile = profile;
        _time = timeProvider;
        _initialDelay = initialDelay > TimeSpan.Zero ? initialDelay : null;
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
                _burstStartedAt = _time.GetUtcNow(); // (re)start the burst window
            else
                _burstStartedAt = null;

            if (oldState == HostState.Unreachable && newState != HostState.Unreachable)
                _releaseTcs.TrySetResult(); // unpark any waiter
            if (newState == HostState.Unreachable && oldState != HostState.Unreachable)
                _releaseTcs = NewTcs();
        }
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
                else if (_initialDelay is { } initial)
                {
                    _initialDelay = null;
                    delay = initial;
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
        bool burstEligible = _state is HostState.Faltering or HostState.Recovering or HostState.Down;
        if (burstEligible && _burstStartedAt is { } start &&
            _time.GetUtcNow() - start < _profile.BurstMaxDuration)
        {
            return _profile.BurstInterval;
        }
        return _profile.NormalInterval;
    }
}
