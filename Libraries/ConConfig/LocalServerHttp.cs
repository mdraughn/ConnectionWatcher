using System.Net;
using System.Net.Sockets;

namespace ConConfig;

/// <summary>HTTP plumbing shared by the CLI and the tray for talking to the server URL.</summary>
public static class LocalServerHttp
{
    /// <summary>
    /// A handler that connects to every address the host resolves to at once and
    /// keeps the first that answers. `localhost` is ::1 and 127.0.0.1, the default
    /// listen URL is IPv4 only, and Windows spends ~2 s retrying each refused
    /// address — tried in turn, a dead service reads as a timeout and a live one
    /// takes 2 s to answer.
    /// </summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler { ConnectCallback = ConnectToFirstAnswerAsync };

    private static async ValueTask<Stream> ConnectToFirstAnswerAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        using var losers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = addresses.Select(a => ConnectAsync(new IPEndPoint(a, context.DnsEndPoint.Port), losers.Token)).ToList();
        SocketException? last = null;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            try
            {
                var socket = await done;
                losers.Cancel();
                // Close any loser that connected in the same instant; observe the rest's failures.
                foreach (var rest in pending)
                    _ = rest.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; });
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static async Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
