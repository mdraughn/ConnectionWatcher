using System.Diagnostics;
using System.Text.Json;

namespace ConConfig;

/// <param name="Status">The `status` word from /api/v1/health, or null on failure.</param>
/// <param name="Failure">"refused", "timed out", "HTTP 503", ... or null on success.</param>
public sealed record HealthResult(string? Status, string? Failure, long ElapsedMs);

public interface IHealthProbe
{
    Task<HealthResult> ProbeAsync(string serverUrl, CancellationToken ct);
}

/// <summary>GET {serverUrl}/api/v1/health, 3 s budget. Read-only. Shared by the CLI and the tray's Settings Test button.</summary>
public sealed class HttpHealthProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null) : IHealthProbe
{
    // The shared handler races ::1 and 127.0.0.1, so a dead localhost port says "refused".
    private readonly HttpClient _http = new(handler ?? LocalServerHttp.CreateHandler()) { Timeout = timeout ?? TimeSpan.FromSeconds(3) };

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
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or ArgumentException or NotSupportedException)
        {
            // A URL HttpClient cannot use: no scheme ("localhost:8080" reads as scheme "localhost") or a bare "http://".
            return new HealthResult(null, "invalid URL", watch.ElapsedMilliseconds);
        }
    }
}
