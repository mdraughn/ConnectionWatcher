using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence;

/// <summary>Well-known setting keys.</summary>
public static class SettingKeys
{
    public const string AnchorPool = "Verdict.AnchorPool";
    public const string WeatherThreshold = "Verdict.WeatherThreshold";
    public const string MinuteRetentionDays = "Retention.MinuteRetentionDays";
    public const string HourlyRetentionMonths = "Retention.HourlyRetentionMonths";
    public const string TransitionRetentionMonths = "Retention.TransitionMonths";
    public const string RetentionRunHourUtc = "Retention.RetentionRunHourUtc";
    public const string RetentionLastRunUtc = "Retention.LastRunUtc";

    public const string MqttEnabled = "Mqtt.Enabled";
    public const string MqttHost = "Mqtt.Host";
    public const string MqttPort = "Mqtt.Port";
    public const string MqttUsername = "Mqtt.Username";
    public const string MqttPassword = "Mqtt.Password";
    public const string MqttBaseTopic = "Mqtt.BaseTopic";
    public const string MqttDiscoveryPrefix = "Mqtt.DiscoveryPrefix";

    public const string WebhookEnabled = "Webhook.Enabled";
    public const string WebhookUrls = "Webhook.Urls";
    public const string WebhookVerdictOnly = "Webhook.VerdictOnly";

    public const string HeartbeatUrl = "Heartbeat.Url";
    public const string HeartbeatIntervalMinutes = "Heartbeat.IntervalMinutes";

    /// <summary>Warm restart: seed presumed states from the DB when the gap
    /// since the watcher was last alive is at most this many minutes; 0 or
    /// less disables all seeding (process start AND config rebuilds).</summary>
    public const string WarmRestartMaxGapMinutes = "Engine.WarmRestartMaxGapMinutes";

    /// <summary>Master enable: "false" parks the engine (no pings, rollups,
    /// verdicts, or notifications) while the web UI and APIs keep serving as
    /// a read-only archive. Anything else (including absent) means enabled.</summary>
    public const string EngineEnabled = "Engine.Enabled";

    /// <summary>Set by the clone wizard: the node this one was cloned from.
    /// Drives the take-over card and the both-active health warning.</summary>
    public const string CloneSourceUrl = "Clone.SourceUrl";

    /// <summary>Never leaves the machine via the snapshot export: credentials
    /// and capability URLs (a webhook or heartbeat URL grants whoever holds it
    /// the power to impersonate or silence this instance).</summary>
    public static readonly string[] SecretKeys = [MqttPassword, WebhookUrls, HeartbeatUrl];

    /// <summary>Node identity, not configuration: excluded from snapshot
    /// export AND preserved locally across snapshot import — a take-over's
    /// final sync from a just-disabled source must not import Enabled=false.</summary>
    public static readonly string[] PerNodeKeys = [EngineEnabled, CloneSourceUrl];
}

/// <summary>
/// Typed access to the Settings table. Seeded from appsettings at startup for
/// keys not yet present; the table is authoritative afterwards. Writes to any
/// Verdict.* key raise SettingsChanged (the host debounces an engine rebuild).
/// </summary>
public class SettingsStore(IDbContextFactory<ConWatchDbContext> dbFactory)
{
    public event Action? SettingsChanged;

    /// <summary>Raised on Mqtt.*/Webhook.* writes; notifiers reconfigure live.</summary>
    public event Action? NotifierSettingsChanged;

    public async Task SeedMissingAsync(IReadOnlyDictionary<string, string> defaults)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.Settings.Select(s => s.Key).ToHashSetAsync();
        foreach (var (key, value) in defaults)
        {
            if (!existing.Contains(key))
                db.Settings.Add(new SettingRow { Key = key, Value = value });
        }
        await db.SaveChangesAsync();
    }

    public async Task<string?> GetAsync(string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.Settings.FindAsync(key))?.Value;
    }

    public async Task<int> GetIntAsync(string key, int fallback) =>
        int.TryParse(await GetAsync(key), out var value) ? value : fallback;

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value);
    }

    public async Task SetManyAsync(IReadOnlyDictionary<string, string?> values)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        foreach (var (key, value) in values)
        {
            var row = await db.Settings.FindAsync(key);
            if (value is null)
            {
                if (row is not null)
                    db.Settings.Remove(row);
            }
            else if (row is null)
            {
                db.Settings.Add(new SettingRow { Key = key, Value = value });
            }
            else
            {
                row.Value = value;
            }
        }
        await db.SaveChangesAsync();

        if (values.Keys.Any(k =>
                k.StartsWith("Verdict.", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase)))
            SettingsChanged?.Invoke();
        if (values.Keys.Any(k =>
                k.StartsWith("Mqtt.", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("Webhook.", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("Heartbeat.", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase)))
            NotifierSettingsChanged?.Invoke();
    }
}
