using System.Net;

namespace ConEngine.Network
{
    /// <summary>
    /// Interface for domain name resolution implementations.
    /// </summary>
    public interface IDomainNameResolver : IDisposable
    {
        /// <summary>
        /// Resolves the configured domain name to IP addresses.
        /// </summary>
        /// <param name="domainName"></param>
        /// <param name="timeoutMs">Timeout in milliseconds for the resolution operation.</param>
        /// <returns>A task that represents the asynchronous resolution operation. The task result contains the resolution result.</returns>
        Task<DomainResolutionResult> ResolveAsync(string domainName, int timeoutMs);
    }
}