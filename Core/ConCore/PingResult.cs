using System.Net;

namespace ConCore
{
    /// <summary>
    /// Represents an immutable result of a ping operation.
    /// </summary>
    public record PingResult
    {
        /// <summary>
        /// Initializes a new instance of the PingResult.
        /// </summary>
        /// <param name="ipAddress">The IP address that was pinged.</param>
        /// <param name="success">Whether the ping was successful.</param>
        /// <param name="roundtripTime">The round-trip time in milliseconds.</param>
        /// <param name="message"></param>
        public PingResult(IPAddress ipAddress, bool success, long roundtripTime, string? message = null,
            DateTimeOffset? timestamp = null)
        {
            Timestamp = timestamp ?? DateTimeOffset.UtcNow;
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Success = success;
            RoundtripTime = roundtripTime;
            Message = message ?? string.Empty;
        }

        /// <summary>
        /// Gets the timestamp of the ping result.
        /// </summary>    
        public DateTimeOffset Timestamp { get; }

        /// <summary>
        /// Gets the IP address that was pinged.
        /// </summary>
        public IPAddress IpAddress { get; }

        /// <summary>
        /// Gets whether the ping operation was successful.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Gets the round-trip time in milliseconds.
        /// </summary>
        public long RoundtripTime { get; }

        /// <summary>
        /// Gets any attached message (e.g., error description).
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Returns a string representation of this PingResult.
        /// </summary>
        public override string ToString()
        {
            return Success
                ? $"Ping to {IpAddress} succeeded in {RoundtripTime}ms"
                : $"Ping to {IpAddress} failed";
        }
    }
}