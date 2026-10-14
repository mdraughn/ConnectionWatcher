using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConCore;

namespace ConInfrastructure;

/// <summary>
/// Domain name resolver over System.Net.Dns. Cancellation of the token given
/// at construction propagates; a timeout or a DNS error is a failed result
/// whose Status says why.
/// </summary>
public sealed class DomainNameResolverDns(CancellationToken cancellationToken) : IDomainNameResolver
{
    public async Task<DomainResolutionResult> ResolveAsync(string domainName, int timeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(domainName, timeoutCts.Token).ConfigureAwait(false);
            return new DomainResolutionResult(domainName, success: true, addresses,
                status: "Success", resolutionTimeMs: stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("Timeout");
        }
        catch (SocketException ex)
        {
            return Failed(ex.SocketErrorCode switch
            {
                SocketError.HostNotFound => "Host not found",
                SocketError.TryAgain => "Temporary DNS failure",
                SocketError.NoRecovery => "Non-recoverable DNS error",
                SocketError.NoData => "No DNS data found",
                _ => $"DNS error: {ex.Message}",
            });
        }
        catch (ArgumentException ex)
        {
            return Failed($"Invalid domain name: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed($"Error: {ex.Message}");
        }

        DomainResolutionResult Failed(string status) => new(domainName, success: false,
            Array.Empty<IPAddress>(), status, stopwatch.ElapsedMilliseconds);
    }

    public void Dispose()
    {
        // Nothing to release: Dns owns no per-instance resources.
    }
}
