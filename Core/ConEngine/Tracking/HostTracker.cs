using System.Net;
using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using Microsoft.Extensions.Logging;

namespace ConEngine.Tracking;

/// <summary>
/// Tracks historic ping results for a single host using a sliding window and
/// drives the per-host state machine. Every command returns its outcome and
/// also publishes it on the observable streams, always after the tracker's
/// own lock has been released.
/// </summary>
public class HostTracker
{
    private readonly CircularBuffer<PingHistoryEntry> _history;
    private readonly int _minUsableSample;
    private readonly Lock _lock = new();
    private readonly ILogger<HostTracker> _log;
    private readonly HostAnalysisEngine _analysisEngine;

    private readonly Subject<HostStatus> _hostStatusSubject = new();
    private readonly Subject<AnalysisTransition> _stateChangeSubject = new();

    /// <summary>The IP address of the host being tracked (updated on a DNS re-point).</summary>
    public IPAddress HostAddress { get; private set; }

    /// <param name="hostAddress">The IP address of the host to track.</param>
    /// <param name="profile">Ping profile providing analysis settings.</param>
    /// <param name="timeProvider">Clock used for commanded transitions.</param>
    /// <param name="log">Logger.</param>
    public HostTracker(IPAddress hostAddress, PingProfile profile, TimeProvider timeProvider, ILogger<HostTracker> log)
    {
        _log = log;
        HostAddress = hostAddress ?? throw new ArgumentNullException(nameof(hostAddress));
        ArgumentNullException.ThrowIfNull(profile);

        _history = new CircularBuffer<PingHistoryEntry>(profile.HistorySize);
        _minUsableSample = profile.MinUsableSample;
        _analysisEngine = new HostAnalysisEngine(profile, hostAddress, timeProvider);
    }

    /// <summary>Current state of the host state machine.</summary>
    public HostState CurrentState => _analysisEngine.CurrentState;

    /// <summary>True while running on an unconfirmed warm-restart presumption.</summary>
    public bool Assumed => _analysisEngine.Assumed;

    /// <summary>
    /// Stream of status snapshots (one per recorded sample or command).
    /// Diagnostic surface: the engine's own consumer, TargetMonitor, uses the
    /// AnalysisOutcome each command returns; these streams exist so a test or
    /// a tool can watch a tracker without wrapping it.
    /// </summary>
    public IObservable<HostStatus> HostStatusSource => _hostStatusSubject;

    /// <summary>Stream of state transitions with structured reasons (see <see cref="HostStatusSource"/>).</summary>
    public IObservable<AnalysisTransition> HostStateChangeSource => _stateChangeSubject;

    /// <summary>Gets the current count of items in the history buffer.</summary>
    public int HistoryCount
    {
        get { lock (_lock) { return _history.Count; } }
    }

    /// <summary>Gets the minimum number of samples required for statistics to be considered usable.</summary>
    public int MinUsableSample => _minUsableSample;

    /// <summary>Warm restart: adopt a presumed state silently (no transition).</summary>
    public AnalysisOutcome? Seed(HostState state)
    {
        AnalysisOutcome? outcome;
        lock (_lock)
        {
            outcome = _analysisEngine.Seed(state);
        }
        Publish(outcome);
        return outcome;
    }

    /// <summary>DNS moved the host: keep state and history, change the address.</summary>
    public void UpdateAddress(IPAddress address)
    {
        lock (_lock)
        {
            HostAddress = address;
            _analysisEngine.UpdateAddress(address);
        }
    }

    /// <summary>Records a new ping result in the history and recalculates state.</summary>
    public AnalysisOutcome? RecordPingResult(PingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var entry = PingHistoryEntry.FromPingResult(result);

        AnalysisOutcome? outcome;
        lock (_lock)
        {
            _history.Add(entry);
            outcome = _analysisEngine.Recalculate(Sample());
        }
        Publish(outcome);
        return outcome;
    }

    /// <summary>Commanded by topology: an ancestor is Down.</summary>
    public AnalysisOutcome? MarkUnreachable(Guid ancestorId)
    {
        AnalysisOutcome? outcome;
        lock (_lock)
        {
            outcome = _analysisEngine.MarkUnreachable(ancestorId);
        }
        Publish(outcome);
        return outcome;
    }

    /// <summary>Commanded by topology: the ancestor recovered; clear history and re-prove.</summary>
    public AnalysisOutcome? Release()
    {
        AnalysisOutcome? outcome;
        lock (_lock)
        {
            _history.Clear();
            outcome = _analysisEngine.Release();
        }
        Publish(outcome);
        return outcome;
    }

    /// <summary>History entries with Timestamp &gt;= since, oldest first (a snapshot).</summary>
    public IReadOnlyList<PingHistoryEntry> EntriesSince(DateTimeOffset since)
    {
        lock (_lock)
        {
            return _history.InReverseOrder().TakeWhile(e => e.Timestamp >= since).Reverse().ToList();
        }
    }

    /// <summary>
    /// The most recent MinUsableSample entries, newest first. Callers hold
    /// <see cref="_lock"/>; the analysis engine consumes it immediately.
    /// </summary>
    private IEnumerable<PingHistoryEntry> Sample()
    {
        int sampleCount = Math.Min(_history.Count, _minUsableSample);
        return _history.InReverseOrder().Take(sampleCount);
    }

    private void Publish(AnalysisOutcome? outcome)
    {
        if (outcome is null)
            return;
        _hostStatusSubject.OnNext(outcome.Status);
        if (outcome.Transition is { } transition)
            _stateChangeSubject.OnNext(transition);
    }

    public override string ToString() => $"{HostAddress}: {CurrentState} (last {HistoryCount})";
}
