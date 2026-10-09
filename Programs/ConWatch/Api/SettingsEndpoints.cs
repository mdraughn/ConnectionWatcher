using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;

namespace ConWatch.Api;

/// <summary>Read/write the UI-editable settings (verdict + retention).</summary>
public static class SettingsEndpoints
{
    public static void MapSettings(RouteGroupBuilder locked, RouteGroupBuilder open)
    {
        // Health warnings feed banners on open pages; settings themselves are
        // admin-only (the GET includes the MQTT broker password).
        open.MapGet("/config/health", async (ConfigHealth health,
            ConWatch.Clone.CloneHealth cloneHealth, WatcherHostService host) =>
        {
            var warnings = (await health.EvaluateAsync()).ToList();
            if (await cloneHealth.EvaluateAsync() is { } cloneWarning)
                warnings.Add(cloneWarning);
            warnings.AddRange(RuntimeHealth.RefusalWarnings(host.Engine));
            return new { warnings };
        });

        locked.MapGet("/settings", async (SettingsStore store) => await store.GetAllAsync());

        locked.MapPut("/settings", async (Dictionary<string, string?> values, SettingsStore store,
            IDbContextFactory<ConWatchDbContext> dbFactory) =>
        {
            var errors = new List<string>();
            foreach (var (key, value) in values)
            {
                switch (key)
                {
                    case SettingKeys.AnchorPool:
                        if (value is { Length: > 0 })
                        {
                            await using var db = await dbFactory.CreateDbContextAsync();
                            if (!await db.Pools.AnyAsync(p => p.Name == value))
                                errors.Add($"'{value}' is not an existing pool.");
                        }
                        break;
                    case SettingKeys.WeatherThreshold:
                        if (!int.TryParse(value, out var threshold) || threshold < 1)
                            errors.Add("WeatherThreshold must be an integer ≥ 1.");
                        break;
                    case SettingKeys.MinuteRetentionDays:
                        if (!int.TryParse(value, out var days) || days < 1 || days > 3650)
                            errors.Add("MinuteRetentionDays must be 1–3650.");
                        break;
                    case SettingKeys.HourlyRetentionMonths:
                        if (!int.TryParse(value, out var months) || months < 1 || months > 120)
                            errors.Add("HourlyRetentionMonths must be 1–120.");
                        break;
                    case SettingKeys.TransitionRetentionMonths:
                        if (!int.TryParse(value, out var trMonths) || trMonths < 1 || trMonths > 120)
                            errors.Add("Retention.TransitionMonths must be 1–120.");
                        break;
                    case SettingKeys.RetentionRunHourUtc:
                        if (!int.TryParse(value, out var hour) || hour < 0 || hour > 23)
                            errors.Add("RetentionRunHourUtc must be 0–23.");
                        break;
                    case SettingKeys.RetentionLastRunUtc:
                        errors.Add("Retention.LastRunUtc is read-only.");
                        break;
                    case SettingKeys.MqttPort:
                        if (!int.TryParse(value, out var mqttPort) || mqttPort < 1 || mqttPort > 65535)
                            errors.Add("Mqtt.Port must be 1–65535.");
                        break;
                    case SettingKeys.MqttEnabled:
                    case SettingKeys.WebhookEnabled:
                    case SettingKeys.WebhookVerdictOnly:
                    case SettingKeys.WebhookSkipExplained:
                    case SettingKeys.EngineEnabled:
                    case SettingKeys.TracerouteAsnLookup:
                        if (value is not (null or "true" or "false"))
                            errors.Add($"{key} must be 'true' or 'false'.");
                        break;
                    case SettingKeys.CloneSourceUrl:
                    case SettingKeys.CloneIncludeLocalDevices:
                        errors.Add("Clone.SourceUrl and Clone.IncludeLocalDevices are set by the clone wizard, not edited.");
                        break;
                    case SettingKeys.MqttHost:
                    case SettingKeys.MqttUsername:
                    case SettingKeys.MqttPassword:
                    case SettingKeys.MqttBaseTopic:
                    case SettingKeys.MqttDiscoveryPrefix:
                        break; // free text; null clears
                    case SettingKeys.HeartbeatUrl:
                        if (value is { Length: > 0 } &&
                            (!Uri.TryCreate(value, UriKind.Absolute, out var hbUri) ||
                             hbUri.Scheme is not ("http" or "https")))
                            errors.Add("Heartbeat.Url must be an absolute http(s) URL.");
                        break;
                    case SettingKeys.HeartbeatIntervalMinutes:
                        if (!int.TryParse(value, out var hbMinutes) || hbMinutes < 1 || hbMinutes > 1440)
                            errors.Add("Heartbeat.IntervalMinutes must be 1–1440.");
                        break;
                    case SettingKeys.WebhookUrls:
                        foreach (var line in (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) ||
                                uri.Scheme is not ("http" or "https"))
                                errors.Add($"Webhook URL '{line}' is not a valid http(s) URL.");
                        }
                        break;
                    default:
                        errors.Add($"Unknown setting '{key}'.");
                        break;
                }
            }
            if (errors.Count > 0)
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, [.. errors]);

            await store.SetManyAsync(values);
            return Results.NoContent();
        });
    }
}
