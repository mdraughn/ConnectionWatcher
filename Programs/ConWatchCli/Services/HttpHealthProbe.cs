using System.Diagnostics;
using System.Text.Json;

namespace ConWatchCli.Services;

/// <summary>GET {serverUrl}/api/v1/health, 3 s budget. Read-only; the CLI's only HTTP call.</summary>
public sealed class HttpHealthProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null) : IHealthProbe
{
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = timeout ?? TimeSpan.FromSeconds(3) };

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
