using System.Net;

namespace ConEngine
{
    /// <summary>
    /// Interface for ping target implementations.
    /// </summary>
    public interface IPingTarget : IDisposable
    {
        /// <summary>
        /// Gets the IP address that this ping target is configured to ping.
        /// </summary>
        IPAddress IpAddress { get; }

        /// <summary>
        /// Launches an async ICMP echo request to the configured IP address.
        /// </summary>
        /// <returns>A task that represents the asynchronous ping operation. The task result contains the ping result.</returns>
        Task<PingResult> PingAsync();
    }
}