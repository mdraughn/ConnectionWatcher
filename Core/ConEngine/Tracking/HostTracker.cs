using ConCore;
using ConEngine.Configuration;
using System.Net;
using System.Reactive.Subjects;

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

        /// <summary>
        /// Gets the IP address of the host being tracked.
        /// </summary>
        public IPAddress HostAddress { get; }

        // Observable host status
        private readonly ReplaySubject<HostStatus> _hostStatusSubject = new(bufferSize: 1);
        public IObservable<HostStatus> HostStatusSource => _hostStatusSubject;

        private readonly HostAnalysisEngine _analysisEngine;

        /// <summary>
        /// Initializes a new instance of the HostTracker class with options.
        /// </summary>
        /// <param name="hostAddress">The IP address of the host to track.</param>
        /// <param name="options">Configuration options for host tracking.</param>
        public HostTracker(IPAddress hostAddress, HostTrackingOptions options)
        {
            HostAddress = hostAddress ?? throw new ArgumentNullException(nameof(hostAddress));

            ArgumentNullException.ThrowIfNull(options);

            _history = new CircularBuffer<PingHistoryEntry>(options.HistorySize);
            _minUsableSample = options.MinUsableSample;
            _analysisEngine = new HostAnalysisEngine(options,hostAddress);
            _analysisEngine.HostStatusSource.Subscribe((status =>
            {
                _hostStatusSubject.OnNext(status);
            }));
        }

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
        /// Records a new ping result in the history with a specific timestamp.
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
        /// This method efficiently extracts items directly from the circular buffer without
        /// creating intermediate arrays.
        /// </summary>
        /// <returns>A span containing up to MinUsableSample most recent entries.</returns>
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