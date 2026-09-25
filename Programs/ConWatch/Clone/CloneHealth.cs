using System.Text.Json;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Settings;

namespace ConWatch.Clone;

/// <summary>
/// Warns when the node this one was cloned from appears to still be
/// monitoring while this node is enabled too (double pings, MQTT entity
/// fights). Probe failures stay silent — an unreachable or decommissioned
/// source is the normal end state. Results are cached briefly so the health
/// banner doesn't hammer the source.
/// </summary>
public class CloneHealth(IHttpClientFactory httpFactory, SettingsStore settings,
    TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private HealthWarning? _cached;

    public async Task<HealthWarning?> EvaluateAsync()
    {
        // Cheap local checks stay OUTSIDE the cache: toggling this node must
        // reflect immediately; only the network probe result is cached.
        if (await settings.GetAsync(SettingKeys.EngineEnabled) == "false")
            return null; // we are the archive; no conflict possible
        var stored = await settings.GetAsync(SettingKeys.CloneSourceUrl);
        if (!Uri.TryCreate(stored, UriKind.Absolute, out var source))
            return null;

        lock (_lock)
        {
            if (time.GetUtcNow() - _checkedAt < CacheFor)
                return _cached;
        }
        var warning = await ProbeAsync(source);
        lock (_lock)
        {
            _checkedAt = time.GetUtcNow();
            _cached = warning;
        }
        return warning;
    }

    private async Task<HealthWarning?> ProbeAsync(Uri source)
    {
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            var client = httpFactory.CreateClient(CloneService.HttpClientName);
            var response = await client.GetAsync(new Uri(source, "api/v1/health"), cts.Token);
            var body = JsonSerializer.Deserialize<JsonElement>(
                await response.Content.ReadAsStringAsync(cts.Token));
            if (body.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean())
                return new HealthWarning("clone-source-active",
                    $"The node this one was cloned from ({source}) appears to still be " +
                    "monitoring — two active nodes double-ping and fight over MQTT entities. " +
                    "Finish the take-over, or disable one of them.");
        }
        catch
        {
            // Unreachable, non-JSON, or pre-enabled-flag build: stay silent.
        }
        return null;
    }
}
