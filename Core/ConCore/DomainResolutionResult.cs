using System.Net;

namespace ConCore
{
    /// <summary>
    /// Represents an immutable result of a domain name resolution operation.
    /// </summary>
    public record DomainResolutionResult
    {
        /// <summary>
        /// Initializes a new instance of the DomainResolutionResult.
        /// </summary>
        /// <param name="domainName">The domain name that was resolved.</param>
        /// <param name="success">Whether the resolution was successful.</param>
        /// <param name="ipAddresses">The collection of resolved IP addresses in order returned by DNS.</param>
        /// <param name="status">The status of the resolution operation.</param>
        /// <param name="resolutionTimeMs">The time taken for resolution in milliseconds.</param>
        public DomainResolutionResult(string domainName, bool success, IReadOnlyList<IPAddress> ipAddresses, string? status = null, long resolutionTimeMs = 0)
        {
            DomainName = domainName ?? throw new ArgumentNullException(nameof(domainName));
            Success = success;
            IpAddresses = ipAddresses ?? throw new ArgumentNullException(nameof(ipAddresses));
            Status = status ?? (success ? "Success" : "Failed");
            ResolutionTimeMs = resolutionTimeMs;
        }

        /// <summary>
        /// Gets the domain name that was resolved.
        /// </summary>
        public string DomainName { get; }

        /// <summary>
        /// Gets whether the domain resolution was successful.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Gets the resolved IP addresses in the order returned by DNS.
        /// </summary>
        public IReadOnlyList<IPAddress> IpAddresses { get; }

        /// <summary>
        /// Gets the status description of the resolution operation.
        /// </summary>
        public string Status { get; }

        /// <summary>
        /// Gets the time taken for resolution in milliseconds.
        /// </summary>
        public long ResolutionTimeMs { get; }

        /// <summary>
        /// Gets the count of resolved IP addresses.
        /// </summary>
        public int AddressCount => IpAddresses.Count;

        /// <summary>
        /// Gets the first resolved IP address, or null if none were resolved.
        /// </summary>
        public IPAddress? FirstAddress => IpAddresses.FirstOrDefault();

        /// <summary>
        /// Gets all IPv4 addresses from the resolved addresses.
        /// </summary>
        public IEnumerable<IPAddress> IPv4Addresses => IpAddresses.Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

        /// <summary>
        /// Gets all IPv6 addresses from the resolved addresses.
        /// </summary>
        public IEnumerable<IPAddress> IPv6Addresses => IpAddresses.Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

        /// <summary>
        /// Returns a string representation of this DomainResolutionResult.
        /// </summary>
        public override string ToString()
        {
            if (Success)
            {
                return $"Resolved {DomainName} to {AddressCount} address(es) in {ResolutionTimeMs}ms: {string.Join(", ", IpAddresses)}";
            }
            else
            {
                return $"Failed to resolve {DomainName}: {Status}";
            }
        }

        /// <summary>
        /// Returns a detailed string representation of this DomainResolutionResult.
        /// </summary>
        public string ToDetailedString()
        {
            if (Success)
            {
                var ipv4Count = IPv4Addresses.Count();
                var ipv6Count = IPv6Addresses.Count();
                return $"Domain: {DomainName}, Success: {Success}, Total: {AddressCount} ({ipv4Count} IPv4, {ipv6Count} IPv6), Time: {ResolutionTimeMs}ms, Status: {Status}";
            }
            else
            {
                return $"Domain: {DomainName}, Success: {Success}, Status: {Status}, Time: {ResolutionTimeMs}ms";
            }
        }
    }
}