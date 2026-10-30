using System.Net;
using System.Net.NetworkInformation;

namespace ConInfrastructure.Net;

/// <summary>One-shot ICMP echo (discovery, scouting); the long-lived probe is PingTransceiverIcmp.</summary>
public static class IcmpEcho
{
    /// <summary>One echo. Round-trip ms on a reply; null on no reply or any failure except cancellation,
    /// which throws promptly on every platform (the PingTransceiverIcmp WaitAsync fix).</summary>
    public static async Task<long?> TryPingAsync(IPAddress address, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            return await TryPingAsync(address, timeout,
                (a, t, token) => ping.SendPingAsync(a, t, buffer: null, options: null, token), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;       // e.g. a Ping that cannot be created
        }
    }

    internal static async Task<long?> TryPingAsync(IPAddress address, TimeSpan timeout,
        Func<IPAddress, TimeSpan, CancellationToken, Task<PingReply>> send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // Windows only checks the token after the native call returns, so WaitAsync makes cancellation
            // prompt there too; the abandoned request finishes on its own and Ping.Dispose defers cleanup.
            var sending = send(address, timeout, ct);
            PingReply reply;
            try
            {
                reply = await sending.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = sending.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;       // PingException, SocketException, ...: no answer
        }
    }
}
