using System.Net;
using System.Net.NetworkInformation;
using ConCore;

namespace ConInfrastructure;

/// <summary>
/// High-performance ping target implementation using System.Net.NetworkInformation.Ping.
/// This is a drop-in replacement for PingTarget with significantly better performance.
/// </summary>
public class PingTranceiverIcmp : IPingTransceiver
{
    private readonly IPAddress _ipAddress;
    private readonly CancellationToken _cancellationToken;
    private readonly Ping _ping;
    private bool _disposed = false;

    /// <summary>
    /// Initializes a new instance of the PingTarget class.
    /// </summary>
    /// <param name="ipAddress">The IP address to ping.</param>
    /// <param name="cancellationToken">Cancellation token for async operations.</param>
    /// <param name="timeout">Timeout in milliseconds (default: 5000ms).</param>
    public PingTranceiverIcmp(IPAddress ipAddress, CancellationToken cancellationToken)
    {
        _ipAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
        _cancellationToken = cancellationToken;
        _ping = new Ping();
    }

    /// <summary>
    /// Gets the IP address that this ping target is configured to ping.
    /// </summary>
    public IPAddress IpAddress => _ipAddress;

    /// <summary>
    /// Launch an async ICMP echo request to the configured IP address.
    /// Uses the optimized System.Net.NetworkInformation.Ping implementation for better performance.
    /// </summary>
    /// <returns>A task that represents the asynchronous ping operation. The task result contains the ping result.</returns>
    public async Task<PingResult> PingAsync(int timeoutMs)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PingTranceiverIcmp));

        try
        {
            // Use the built-in Ping class which is optimized and doesn't require raw sockets
            PingReply reply = await _ping.SendPingAsync(_ipAddress, timeoutMs).ConfigureAwait(false);
            return new PingResult(
                _ipAddress,
                reply.Status == IPStatus.Success,
                reply.RoundtripTime,
                reply.Status.ToString()
            );
        }
        catch (PingException ex)
        {
            // Handle ping-specific exceptions
            string errorMessage = $"PingException: {ex.Message}";
            return new PingResult(_ipAddress, false, 0, errorMessage);
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation
            return new PingResult(_ipAddress, false, 0, "Cancelled");
        }
        catch (Exception ex)
        {
            // Handle any other exceptions
            string errorMessage = $"Error: {ex.Message}";
            return new PingResult(_ipAddress, false, 0, errorMessage);
        }
    }

    /// <summary>
    /// Releases all resources used by the PingTarget.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the PingTarget and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Dispose managed resources
                _ping?.Dispose();
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Finalizer for PingTarget to ensure cleanup if Dispose is not called.
    /// </summary>
    ~PingTranceiverIcmp()
    {
        Dispose(false);
    }
}