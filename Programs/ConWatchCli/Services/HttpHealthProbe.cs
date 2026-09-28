using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ConWatchCli.Services;

/// <summary>GET {serverUrl}/api/v1/health, 3 s budget. Read-only; the CLI's only HTTP call.</summary>
public sealed class HttpHealthProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null) : IHealthProbe
{
    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { ConnectCallback = ConnectToFirstAnswerAsync })
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(3),
    };

    /// <summary>
    /// Connects to every address the host resolves to at once and keeps the first
    /// that answers. `localhost` is ::1 and 127.0.0.1, the default listen URL is
    /// IPv4 only, and Windows spends ~2 s retrying each refused address — tried in
    /// turn, a dead service read as "timed out" and a live one took 2 s to answer.
    /// </summary>
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

    public async Task<HealthResult> ProbeAsync(string serverUrl, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.GetAsync($"{serverUrl.TrimEnd('/')}/api/v1/health", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            string? status = null;
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("status", out var s)) status = s.GetString();
            }
            catch (JsonException) { /* not our JSON: status stays null */ }
            var failure = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
            return new HealthResult(status, failure, watch.ElapsedMilliseconds);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthResult(null, "timed out", watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException)
        {
            return new HealthResult(null, "refused", watch.ElapsedMilliseconds);
        }
    }
}
