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
            // Windows only checks the token after the native call returns, so WaitAsync makes cancellation
            // prompt there too; the abandoned request finishes on its own and Ping.Dispose defers cleanup.
            var send = ping.SendPingAsync(address, timeout, buffer: null, options: null, ct);
            PingReply reply;
            try
            {
                reply = await send.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = send.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
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
