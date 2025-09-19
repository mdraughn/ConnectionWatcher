using System.Net;
using System.Net.NetworkInformation;

namespace ConEngine
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
        /// <param name="status">The status of the ping operation.</param>
        public PingResult(IPAddress ipAddress, bool success, long roundtripTime, string? status = null)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Success = success;
            RoundtripTime = roundtripTime;
            Status = status ?? (success ? "Success" : "Failed");
        }

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
        /// Gets the status description of the ping operation.
        /// </summary>
        public string Status { get; }

        /// <summary>
        /// Creates a successful PingResult from a PingReply.
        /// </summary>
        public static PingResult FromReply(IPAddress ipAddress, PingReply reply)
        {
            return new PingResult(
                ipAddress,
                reply.Status == IPStatus.Success,
                reply.RoundtripTime,
                reply.Status.ToString()
            );
        }

        /// <summary>
        /// Creates a failed PingResult with an error message.
        /// </summary>
        public static PingResult FromError(IPAddress ipAddress, string errorMessage)
        {
            return new PingResult(ipAddress, false, 0, errorMessage);
        }

        /// <summary>
        /// Creates a cancelled PingResult.
        /// </summary>
        public static PingResult FromCancellation(IPAddress ipAddress)
        {
            return new PingResult(ipAddress, false, 0, "Cancelled");
        }

        /// <summary>
        /// Returns a string representation of this PingResult.
        /// </summary>
        public override string ToString()
        {
            return Success
                ? $"Ping to {IpAddress} succeeded in {RoundtripTime}ms"
                : $"Ping to {IpAddress} failed: {Status}";
        }
    }
}