using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using System.Net;
using Microsoft.Extensions.Logging;

namespace ConEngine.Tracking
{
    /// <summary>
    /// Tracks historic ping results for a single host using a sliding window.
    /// Maintains the most recent results and provides statistics.
    /// </summary>
    public class HostTracker
    {
        private readonly CircularBuffer<PingHistoryEntry> _history;
        private readonly int _minUsableSample;
        private readonly Lock _lock = new();
        private readonly ILogger<HostTracker> _log;

        /// <summary>
        /// Gets the IP address of the host being tracked.
        /// </summary>
        public IPAddress HostAddress { get; }

        private readonly HostAnalysisEngine _analysisEngine;

        /// <summary>
        /// Initializes a new instance of the HostTracker class.
        /// </summary>
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

        /// <summary>Stream of periodic status snapshots.</summary>
        public IObservable<HostStatus> HostStatusSource => _analysisEngine.HostStatusSource;

        /// <summary>Stream of state transitions with structured reasons.</summary>
        public IObservable<AnalysisTransition> HostStateChangeSource => _analysisEngine.HostStateChangeSource;

        /// <summary>
        /// Gets the capacity of the history buffer.
        /// </summary>
        public int HistoryCapacity => _history.Capacity;

        /// <summary>
        /// Gets the current count of items in the history buffer.
        /// </summary>
        public int HistoryCount => _history.Count;

        /// <summary>
        /// Gets the minimum number of samples required for statistics to be considered usable.
        /// </summary>
        public int MinUsableSample => _minUsableSample;

        /// <summary>
        /// Gets whether the current sample size is sufficient for reliable statistics.
        /// Returns true if HistoryCount >= MinUsableSample.
        /// </summary>
        public bool IsUsableSample => HistoryCount >= _minUsableSample;

        /// <summary>
        /// Records a new ping result in the history and recalculates state.
        /// </summary>
        /// <param name="result">The ping result to record.</param>
        public void RecordPingResult(PingResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var entry = PingHistoryEntry.FromPingResult(result);

            lock (_lock)
            {
                _history.Add(entry);
                Recalculate();
            }
        }

        /// <summary>Commanded by topology: an ancestor is Down.</summary>
        public void MarkUnreachable(Guid ancestorId)
        {
            lock (_lock)
            {
                _analysisEngine.MarkUnreachable(ancestorId);
            }
        }

        /// <summary>Commanded by topology: the ancestor recovered; clear history and re-prove.</summary>
        public void Release()
        {
            lock (_lock)
            {
                _history.Clear();
                _analysisEngine.Release();
            }
        }

        /// <summary>History entries with Timestamp &gt;= since, oldest first.</summary>
        public IEnumerable<PingHistoryEntry> EntriesSince(DateTimeOffset since) =>
            _history.InReverseOrder().TakeWhile(e => e.Timestamp >= since).Reverse();

        private void Recalculate()
        {
            _analysisEngine.Recalculate(Sample());
        }

        /// <summary>
        /// Gets the most recent ping result.
        /// </summary>
        public PingHistoryEntry? LatestResult => _history.GetLatest();

        /// <summary>
        /// Gets the timestamp of the most recent ping.
        /// </summary>
        public DateTimeOffset? LatestTimestamp => LatestResult?.Timestamp;

        /// <summary>
        /// Gets all history entries in chronological order (oldest to newest).
        /// </summary>
        /// <returns>An array of history entries.</returns>
        public PingHistoryEntry[] GetHistory()
        {
            return _history.ToArray();
        }

        /// <summary>
        /// Gets all history entries in reverse chronological order (newest to oldest).
        /// </summary>
        /// <returns>An array of history entries.</returns>
        public PingHistoryEntry[] GetHistoryReversed()
        {
            return _history.ToArrayReversed();
        }

        /// <summary>
        /// Gets the most recent MinUsableSample entries in chronological order (oldest to newest).
        /// If fewer entries exist than MinUsableSample, returns all available entries.
        /// </summary>
        /// <returns>Up to MinUsableSample most recent entries.</returns>
        public IEnumerable<PingHistoryEntry> Sample()
        {
            int sampleCount = Math.Min(_history.Count, _minUsableSample);
            return _history.InReverseOrder().Take(sampleCount);
        }

        /// <summary>
        /// Clears all history entries.
        /// </summary>
        public void ClearHistory()
        {
            _history.Clear();
        }

        /// <summary>
        /// Returns a string representation of this host tracker.
        /// </summary>
        public override string ToString()
        {
            var usabilityIndicator = IsUsableSample ? "" : " (insufficient samples)";
            return $"{HostAddress}: (last {HistoryCount}){usabilityIndicator}";
        }
    }
}
