using System.Net;
using ConCore;
using ConEngine.Configuration;

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

        /// <summary>
        /// Gets the timestamp when tracking started for this host.
        /// </summary>
        public DateTimeOffset TrackingStarted { get; }

        /// <summary>
        /// Initializes a new instance of the HostTracker class with options.
        /// </summary>
        /// <param name="hostAddress">The IP address of the host to track.</param>
        /// <param name="options">Configuration options for host tracking.</param>
        public HostTracker(IPAddress hostAddress, HostTrackingOptions options)
        {
            HostAddress = hostAddress ?? throw new ArgumentNullException(nameof(hostAddress));
            
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _history = new CircularBuffer<PingHistoryEntry>(options.HistorySize);
            _minUsableSample = options.MinUsableSample;
            TrackingStarted = DateTimeOffset.UtcNow;
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
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var entry = PingHistoryEntry.FromPingResult(result);

            lock (_lock)
            {
                _history.Add(entry);
            }
        }

        /// <summary>
        /// Gets the most recent ping result.
        /// </summary>
        public PingHistoryEntry? LatestResult => _history.GetLatest();

        /// <summary>
        /// Gets the oldest ping result in the current window.
        /// </summary>
        public PingHistoryEntry? OldestResult => _history.GetOldest();

        /// <summary>
        /// Gets the timestamp of the most recent ping.
        /// </summary>
        public DateTimeOffset? LatestTimestamp => LatestResult?.Timestamp;

        /// <summary>
        /// Gets the success rate for the current history window as a percentage (0-100).
        /// </summary>
        public double SuccessRate
        {
            get
            {
                var history = GetHistory();
                if (history.Length == 0)
                    return 0.0;

                var successCount = history.Count(e => e.Success);
                return (double)successCount / history.Length * 100.0;
            }
        }

        /// <summary>
        /// Gets the average round-trip time for successful pings in the current history window.
        /// </summary>
        public double? AverageRoundtripTime
        {
            get
            {
                var history = GetHistory();
                var successfulPings = history.Where(e => e.Success).ToArray();
                
                if (successfulPings.Length == 0)
                    return null;

                return successfulPings.Average(e => e.RoundtripTime);
            }
        }

        /// <summary>
        /// Gets the minimum round-trip time for successful pings in the current history window.
        /// </summary>
        public long? MinimumRoundtripTime
        {
            get
            {
                var history = GetHistory();
                var successfulPings = history.Where(e => e.Success).ToArray();
                
                if (successfulPings.Length == 0)
                    return null;

                return successfulPings.Min(e => e.RoundtripTime);
            }
        }

        /// <summary>
        /// Gets the maximum round-trip time for successful pings in the current history window.
        /// </summary>
        public long? MaximumRoundtripTime
        {
            get
            {
                var history = GetHistory();
                var successfulPings = history.Where(e => e.Success).ToArray();
                
                if (successfulPings.Length == 0)
                    return null;

                return successfulPings.Max(e => e.RoundtripTime);
            }
        }

        /// <summary>
        /// Gets the current consecutive success count (from most recent backwards).
        /// </summary>
        public int ConsecutiveSuccessCount
        {
            get
            {
                var history = GetHistoryReversed();
                int count = 0;
                
                foreach (var entry in history)
                {
                    if (entry.Success)
                        count++;
                    else
                        break;
                }
                
                return count;
            }
        }

        /// <summary>
        /// Gets the current consecutive failure count (from most recent backwards).
        /// </summary>
        public int ConsecutiveFailureCount
        {
            get
            {
                var history = GetHistoryReversed();
                int count = 0;
                
                foreach (var entry in history)
                {
                    if (!entry.Success)
                        count++;
                    else
                        break;
                }
                
                return count;
            }
        }

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
        /// Clears all history entries.
        /// </summary>
        public void ClearHistory()
        {
            _history.Clear();
        }

        /// <summary>
        /// Gets a summary of the current tracking state.
        /// </summary>
        public HostTrackerSummary GetSummary()
        {
            lock (_lock)
            {
                return new HostTrackerSummary
                {
                    HostAddress = HostAddress,
                    TrackingStarted = TrackingStarted,
                    SuccessRate = SuccessRate,
                    LatestResult = LatestResult,
                    AverageRoundtripTime = AverageRoundtripTime,
                    MinimumRoundtripTime = MinimumRoundtripTime,
                    MaximumRoundtripTime = MaximumRoundtripTime,
                    ConsecutiveSuccessCount = ConsecutiveSuccessCount,
                    ConsecutiveFailureCount = ConsecutiveFailureCount,
                    HistoryCount = HistoryCount,
                    IsUsableSample = IsUsableSample,
                    MinUsableSample = MinUsableSample
                };
            }
        }

        /// <summary>
        /// Returns a string representation of this host tracker.
        /// </summary>
        public override string ToString()
        {
            var usabilityIndicator = IsUsableSample ? "" : " (insufficient samples)";
            return $"{HostAddress}: {SuccessRate:F1}% success (last {HistoryCount}){usabilityIndicator}";
        }
    }

    /// <summary>
    /// Represents a summary of host tracking statistics.
    /// </summary>
    public record HostTrackerSummary
    {
        public required IPAddress HostAddress { get; init; }
        public DateTimeOffset TrackingStarted { get; init; }
        public double SuccessRate { get; init; }
        public PingHistoryEntry? LatestResult { get; init; }
        public double? AverageRoundtripTime { get; init; }
        public long? MinimumRoundtripTime { get; init; }
        public long? MaximumRoundtripTime { get; init; }
        public int ConsecutiveSuccessCount { get; init; }
        public int ConsecutiveFailureCount { get; init; }
        public int HistoryCount { get; init; }
        public bool IsUsableSample { get; init; }
        public int MinUsableSample { get; init; }

        public override string ToString()
        {
            var usabilityNote = IsUsableSample ? "" : $" (needs {MinUsableSample - HistoryCount} more samples)";
            return $"{HostAddress}: {SuccessRate:F1}% (last {HistoryCount}){usabilityNote}, " +
                   $"Avg: {AverageRoundtripTime:F1}ms";
        }
    }
}