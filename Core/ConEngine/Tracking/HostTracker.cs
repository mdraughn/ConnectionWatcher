using System.Net;
using ConCore;

namespace ConEngine.Tracking
{
    /// <summary>
    /// Tracks historic ping results for a single host using a sliding window.
    /// Maintains the most recent results and provides statistics.
    /// </summary>
    public class HostTracker
    {
        private const int DefaultHistorySize = 10;
        private readonly CircularBuffer<PingHistoryEntry> _history;
        private readonly object _lock = new();

        /// <summary>
        /// Gets the IP address of the host being tracked.
        /// </summary>
        public IPAddress HostAddress { get; }

        /// <summary>
        /// Gets the timestamp when tracking started for this host.
        /// </summary>
        public DateTimeOffset TrackingStarted { get; }

        /// <summary>
        /// Initializes a new instance of the HostTracker class.
        /// </summary>
        /// <param name="hostAddress">The IP address of the host to track.</param>
        /// <param name="historySize">The number of recent results to maintain (default: 10).</param>
        public HostTracker(IPAddress hostAddress, int historySize = DefaultHistorySize)
        {
            HostAddress = hostAddress ?? throw new ArgumentNullException(nameof(hostAddress));
            
            if (historySize <= 0)
                throw new ArgumentException("History size must be greater than zero.", nameof(historySize));

            _history = new CircularBuffer<PingHistoryEntry>(historySize);
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
        /// Records a new ping result in the history.
        /// </summary>
        /// <param name="result">The ping result to record.</param>
        public void RecordPingResult(PingResult result)
        {
            RecordPingResult(result, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Records a new ping result in the history with a specific timestamp.
        /// </summary>
        /// <param name="result">The ping result to record.</param>
        /// <param name="timestamp">The timestamp for this result.</param>
        public void RecordPingResult(PingResult result, DateTimeOffset timestamp)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var entry = PingHistoryEntry.FromPingResult(result, timestamp);

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
        /// Gets whether the most recent ping was successful.
        /// </summary>
        public bool? LatestSuccess => LatestResult?.Success;

        /// <summary>
        /// Gets the round-trip time of the most recent ping.
        /// </summary>
        public long? LatestRoundtripTime => LatestResult?.RoundtripTime;

        /// <summary>
        /// Gets any message from the most recent ping.
        /// </summary>
        public string? LatestMessage => LatestResult?.Message;

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
                    HistoryCount = HistoryCount
                };
            }
        }

        /// <summary>
        /// Returns a string representation of this host tracker.
        /// </summary>
        public override string ToString()
        {
            return $"{HostAddress}: {SuccessRate:F1}% success (last {HistoryCount}), " +
                   $"Latest: {(LatestSuccess == true ? "?" : "?")} {LatestRoundtripTime}ms";
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

        public override string ToString()
        {
            return $"{HostAddress}: {SuccessRate:F1}% (last {HistoryCount}), " +
                   $"Avg: {AverageRoundtripTime:F1}ms";
        }
    }
}