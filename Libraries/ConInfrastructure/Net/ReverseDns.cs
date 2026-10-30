using System.Net;
using System.Net.Sockets;

namespace ConInfrastructure.Net;

/// <summary>Reverse DNS with a cap on how long it may take.</summary>
public static class ReverseDns
{
    /// <summary>The PTR name, or null on timeout, SocketException or ArgumentException. Cancellation throws.
    /// The timeout timer never outlives the call.</summary>
    public static Task<string?> LookupAsync(IPAddress address, TimeSpan timeout, CancellationToken ct) =>
        LookupAsync(address, timeout, Dns.GetHostEntryAsync, ct);

    internal static async Task<string?> LookupAsync(IPAddress address, TimeSpan timeout,
        Func<string, CancellationToken, Task<IPHostEntry>> resolve, CancellationToken ct)
    {
        try
        {
            return (await resolve(address.ToString(), ct).WaitAsync(timeout, ct).ConfigureAwait(false)).HostName;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }
}
