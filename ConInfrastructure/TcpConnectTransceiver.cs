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
        // Synchronous connect on a pool thread, aborted by disposing the socket:
        // Windows' async ConnectEx retries after an RST (~2s before it reports
        // ConnectionRefused), which would turn "Refused" into "TimedOut" under
        // the engine's 1s burst clamp. Sync connect surfaces the RST at once,
        // and Dispose unblocks it when OUR deadline fires.
        var socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        using var abort = timeout.Token.Register(socket.Dispose);
        try
        {
            await Task.Run(() => socket.Connect(new IPEndPoint(ipAddress, port))).ConfigureAwait(false);
            return new PingResult(ipAddress, true, stopwatch.ElapsedMilliseconds, "Accepted");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new PingResult(ipAddress, true, stopwatch.ElapsedMilliseconds, "Refused");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return new PingResult(ipAddress, false, 0, "Cancelled");
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            // The abort disposal interrupts the blocking connect; whatever
            // exception that surfaces as, it means OUR deadline fired first.
            return new PingResult(ipAddress, false, 0, "TimedOut");
        }
        catch (SocketException ex)
        {
            return new PingResult(ipAddress, false, 0, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return new PingResult(ipAddress, false, 0, ex.Message);
        }
        finally
        {
            socket.Dispose();
        }
    }

    public void Dispose() { }
}
