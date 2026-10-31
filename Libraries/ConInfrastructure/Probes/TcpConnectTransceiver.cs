using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConCore;

namespace ConInfrastructure.Probes;

/// <summary>
/// TCP-connect probe: one fresh socket per probe, no data ever sent, closed
/// cleanly (FIN) when accepted. Any stack response counts as a reply —
/// SYN-ACK ("Accepted") or RST ("Refused") — because the question is "can I
/// reach this device", not "is the service healthy". Only silence is loss.
/// The timeout races ConnectAsync because the OS SYN-retry schedule ignores
/// caller deadlines.
/// Windows' TCP stack retries a refused connect for ~2 s before reporting
/// ConnectionRefused (Linux reports the RST immediately), so refusal
/// classification needs a timeout of at least ~2.1 s there — the engine's
/// burst clamp for TCP probes accounts for this.
/// </summary>
public sealed class TcpConnectTransceiver(IPAddress ipAddress, int port,
    CancellationToken cancellationToken) : IPingTransceiver
{
    public IPAddress IpAddress => ipAddress;

    public async Task<PingResult> PingAsync(int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        using var socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ipAddress, port), timeout.Token).ConfigureAwait(false);
            return new PingResult(ipAddress, true, stopwatch.ElapsedMilliseconds, "Accepted");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new PingResult(ipAddress, true, stopwatch.ElapsedMilliseconds, "Refused");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PingResult(ipAddress, false, 0, "TimedOut"); // our own timeout token
        }
        catch (SocketException ex)
        {
            return new PingResult(ipAddress, false, 0, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PingResult(ipAddress, false, 0, ex.Message);
        }
    }

    public void Dispose() { }
}
