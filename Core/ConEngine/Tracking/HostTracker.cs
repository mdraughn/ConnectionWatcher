using System.Net;
using ConCore;
using ConCore.Targets;

namespace ConEngine.Tracking;

/// <summary>
/// The sliding window of ping results and the per-host state machine for one
/// host. Pure: no lock and no streams — every command returns its
/// <see cref="AnalysisOutcome"/>. The owning <see cref="TargetMonitor"/> calls
/// it only under its own lock and publishes the outcome after releasing it.
/// </summary>
public class HostTracker
{
    private readonly CircularBuffer<PingHistoryEntry> _history;
    private readonly int _minUsableSample;
    private readonly HostAnalysisEngine _analysisEngine;

    /// <summary>The IP address of the host being tracked (updated on a DNS re-point).</summary>
    public IPAddress HostAddress { get; private set; }

    public HostTracker(IPAddress hostAddress, PingProfile profile, TimeProvider timeProvider)
    {
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

    /// <summary>Entries in the history window (tests and diagnostics).</summary>
    public int HistoryCount => _history.Count;

    /// <summary>Warm restart: adopt a presumed state silently (no transition).</summary>
    public AnalysisOutcome? Seed(HostState state) => _analysisEngine.Seed(state);

    /// <summary>DNS moved the host: keep state and history, change the address.</summary>
    public void UpdateAddress(IPAddress address)
    {
        HostAddress = address;
        _analysisEngine.UpdateAddress(address);
    }

    /// <summary>Records a new ping result in the history and recalculates state.</summary>
    public AnalysisOutcome? RecordPingResult(PingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _history.Add(PingHistoryEntry.FromPingResult(result));
        return _analysisEngine.Recalculate(Sample());
    }

    /// <summary>Topology names (or withdraws) the upstream root cause of an own Down.</summary>
    public AnalysisOutcome? SetExplanation(Guid? ancestorId) => _analysisEngine.SetExplanation(ancestorId);

    /// <summary>The root cause while shown Unreachable; null otherwise.</summary>
    public Guid? ExplainedBy => _analysisEngine.ExplainedBy;

    /// <summary>Commanded by topology: the root cause recovered; clear history and re-prove.</summary>
    public AnalysisOutcome? Release()
    {
        _history.Clear();
        return _analysisEngine.Release();
    }

    /// <summary>History entries with Timestamp &gt;= since, oldest first (a snapshot).</summary>
    public IReadOnlyList<PingHistoryEntry> EntriesSince(DateTimeOffset since) =>
        _history.InReverseOrder().TakeWhile(e => e.Timestamp >= since).Reverse().ToList();

    /// <summary>The most recent MinUsableSample entries, newest first; consumed immediately.</summary>
    private IEnumerable<PingHistoryEntry> Sample() =>
        _history.InReverseOrder().Take(Math.Min(_history.Count, _minUsableSample));

    public override string ToString() => $"{HostAddress}: {CurrentState} (last {HistoryCount})";
}
