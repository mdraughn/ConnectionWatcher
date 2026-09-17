using System.Net;
using System.Net.NetworkInformation;
using ConCore;

namespace ConInfrastructure;

/// <summary>
/// ICMP echo probe over System.Net.NetworkInformation.Ping. One instance per
/// controller; the token passed at construction cancels an in-flight echo so
/// engine stop and rebuild never wait out a dead host's timeout.
/// </summary>
public class PingTransceiverIcmp : IPingTransceiver
{
    private readonly IPAddress _ipAddress;
    private readonly CancellationToken _cancellationToken;
    private readonly Ping _ping;
    private bool _disposed;

    public PingTransceiverIcmp(IPAddress ipAddress, CancellationToken cancellationToken)
    {
        _ipAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
        _cancellationToken = cancellationToken;
        _ping = new Ping();
    }

    public IPAddress IpAddress => _ipAddress;

    public async Task<PingResult> PingAsync(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // The token goes to Ping for platforms that honor it mid-flight
            // (Linux raw/datagram sockets). Windows only checks it after the
            // native call returns, so WaitAsync makes cancellation prompt
            // there too; the abandoned native request finishes on its own and
            // Ping.Dispose defers cleanup until it does.
            var send = _ping.SendPingAsync(_ipAddress, TimeSpan.FromMilliseconds(timeoutMs),
                buffer: null, options: null, _cancellationToken);
            PingReply reply;
            try
            {
                reply = await send.WaitAsync(_cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = send.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
            return new PingResult(
                _ipAddress,
                reply.Status == IPStatus.Success,
                reply.RoundtripTime,
                reply.Status.ToString()
            );
        }
        catch (PingException ex)
        {
            return new PingResult(_ipAddress, false, 0, $"PingException: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return new PingResult(_ipAddress, false, 0, "Cancelled");
        }
        catch (Exception ex)
        {
            return new PingResult(_ipAddress, false, 0, $"Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ping.Dispose();
    }
}
