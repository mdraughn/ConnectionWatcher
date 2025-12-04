using System.Net;
using ConCore;

namespace ConEngine.Tracking
{
    /// <summary>
    /// Represents a single ping result entry in the history.
    /// </summary>
    public record PingHistoryEntry
    {
        /// <summary>
        /// Gets the timestamp when this ping occurred.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Gets whether the ping was successful.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Gets the round-trip time in milliseconds.
        /// </summary>
        public long RoundtripTime { get; init; }

        /// <summary>
        /// Gets any message associated with this ping result.
        /// </summary>
        public string? Message { get; init; }

        /// <summary>
        /// Creates a PingHistoryEntry from a PingResult.
        /// </summary>
        public static PingHistoryEntry FromPingResult(PingResult result, DateTimeOffset timestamp)
        {
            return new PingHistoryEntry
            {
                Timestamp = timestamp,
                Success = result.Success,
                RoundtripTime = result.RoundtripTime,
                Message = result.Message
            };
        }

        /// <summary>
        /// Returns a string representation of this history entry.
        /// </summary>
        public override string ToString()
        {
            var status = Success ? "?" : "?";
            var time = Success ? $"{RoundtripTime}ms" : Message ?? "Failed";
            return $"{Timestamp:HH:mm:ss.fff} {status} {time}";
        }
    }
}