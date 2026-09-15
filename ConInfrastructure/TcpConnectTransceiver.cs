using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConCore;

namespace ConInfrastructure;

/// <summary>
/// TCP-connect probe: one fresh socket per probe, no data ever sent, closed
/// cleanly (FIN) when accepted. Any stack response counts as a reply —
/// SYN-ACK ("Accepted") or RST ("Refused") — because the question is "can I
/// reach this device", not "is the service healthy". Only silence is loss.
/// The timeout races ConnectAsync because the OS SYN-retry schedule ignores
/// caller deadlines.
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PingResult(ipAddress, false, 0, "Cancelled");
        }
        catch (OperationCanceledException)
        {
            return new PingResult(ipAddress, false, 0, "TimedOut");
        }
        catch (SocketException ex)
        {
            return new PingResult(ipAddress, false, 0, ex.SocketErrorCode.ToString());
        }
    }

    public void Dispose() { }
}
