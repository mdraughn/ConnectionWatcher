using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConCore;

namespace ConInfrastructure
{
    /// <summary>
    /// High-performance domain name resolver implementation using System.Net.Dns.
    /// Provides reliable DNS resolution with proper timeout handling and error management.
    /// </summary>
    public class DomainNameResolverDns : IDomainNameResolver
    {
        private readonly CancellationToken _cancellationToken;
        private bool _disposed = false;

        /// <summary>
        /// Initializes a new instance of the DomainNameResolverImpl class.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for async operations.</param>
        public DomainNameResolverDns(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Resolves the configured domain name to IP addresses.
        /// Uses the built-in System.Net.Dns for reliable DNS resolution.
        /// </summary>
        /// <param name="domainName">The domain name to resolve.</param>
        /// <param name="timeoutMs">Timeout in milliseconds for the resolution operation.</param>
        /// <returns>A task that represents the asynchronous resolution operation. The task result contains the resolution result.</returns>
        public async Task<DomainResolutionResult> ResolveAsync(string domainName, int timeoutMs)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DomainNameResolverDns));

            // Validate domain name
            if (domainName == null)
            {
                throw new ArgumentNullException(nameof(domainName));
            }

            if (string.IsNullOrWhiteSpace(domainName))
                throw new ArgumentException("Domain name cannot be empty or whitespace.", nameof(domainName));

            if (timeoutMs <= 0)
                throw new ArgumentException("Timeout must be greater than zero.", nameof(timeoutMs));

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Create a timeout cancellation token that combines with the instance token
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
                timeoutCts.CancelAfter(timeoutMs);

                // Use the built-in DNS resolver which is optimized and reliable
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(domainName, timeoutCts.Token).ConfigureAwait(false);
                stopwatch.Stop();

                return new DomainResolutionResult(
                    domainName,
                    success: true,
                    addresses,
                    status: "Success",
                    resolutionTimeMs: stopwatch.ElapsedMilliseconds
                );
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                // Handle cancellation from the instance token
                stopwatch.Stop();
                return new DomainResolutionResult(
                    domainName,
                    success: false,
                    Array.AsReadOnly(Array.Empty<IPAddress>()),
                    status: "Cancelled",
                    resolutionTimeMs: stopwatch.ElapsedMilliseconds
                );
            }
            catch (OperationCanceledException)
            {
                // Handle timeout cancellation
                stopwatch.Stop();
                return new DomainResolutionResult(
                    domainName,
                    success: false,
                    Array.AsReadOnly(Array.Empty<IPAddress>()),
                    status: "Timeout",
                    resolutionTimeMs: stopwatch.ElapsedMilliseconds
                );
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                // Handle DNS resolution failures (most common failure mode)
                stopwatch.Stop();
                string errorMessage = ex.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound => "Host not found",
                    System.Net.Sockets.SocketError.TryAgain => "Temporary DNS failure",
                    System.Net.Sockets.SocketError.NoRecovery => "Non-recoverable DNS error",
                    System.Net.Sockets.SocketError.NoData => "No DNS data found",
                    _ => $"DNS error: {ex.Message}"
                };

                return new DomainResolutionResult(
                    domainName,
                    success: false,
                    Array.AsReadOnly(Array.Empty<IPAddress>()),
                    status: errorMessage,
                    resolutionTimeMs: stopwatch.ElapsedMilliseconds
                );
            }
            catch (ArgumentException ex)
            {
                // Handle invalid domain name format
                stopwatch.Stop();
                return new DomainResolutionResult(
                    domainName,
                    success: false,
                    Array.AsReadOnly(Array.Empty<IPAddress>()),
                    status: $"Invalid domain name: {ex.Message}",
                    resolutionTimeMs: stopwatch.ElapsedMilliseconds
                );
            }
            catch (Exception ex)
            {
                // Handle any other unexpected exceptions
                stopwatch.Stop();
                return new DomainResolutionResult(
                    domainName,
                    success: false,
                    Array.AsReadOnly(Array.Empty<IPAddress>()),
                    status: $"Error: {ex.Message}",
                    resolutionTimeMs: stopwatch.ElapsedMilliseconds
                );
            }
        }

        /// <summary>
        /// Releases all resources used by the DomainNameResolverImpl.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the DomainNameResolverImpl and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // No managed resources to dispose for this implementation
                    // DNS operations are handled by the framework
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Finalizer for DomainNameResolverImpl to ensure cleanup if Dispose is not called.
        /// </summary>
        ~DomainNameResolverDns()
        {
            Dispose(false);
        }
    }
}