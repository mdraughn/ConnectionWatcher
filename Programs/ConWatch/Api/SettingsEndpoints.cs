using ConWatch.Health;
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
        open.MapGet("/config/health", async (ConWatch.Health.HealthReport report) =>
            new
            {
                warnings = (await report.AllAsync())
                    .Select(w => new { w.Code, w.Message, Page = w.Page(), PageName = w.PageName() }),
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
                    case SettingKeys.RetentionLastRunUtc:
                        errors.Add("Retention.LastRunUtc is read-only.");
                        break;
                    case SettingKeys.DnsLookupNames:
                        if (value is not null && !ConCore.Dns.DnsLookupName.TryFromJson(value, out _, out var badName))
                            errors.Add(badName is not null
                                ? $"Dns.LookupNames: '{badName}' is not a valid host name (ASCII letters, digits, hyphens and dots; labels of 1-63 characters; at most 253 in all)."
                                : "Dns.LookupNames must be a JSON array of {\"name\", \"expected\"[]} objects.");
                        break;
                    case SettingKeys.DnsRetiredResolvers:
                        errors.Add("Dns.RetiredResolvers is maintained by ConnectionWatcher, not edited.");
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
                        if (value is { Length: > 0 } && !SettingRules.IsHttpUrl(value))
                            errors.Add("Heartbeat.Url must be an absolute http(s) URL.");
                        break;
                    case SettingKeys.WebhookUrls:
                        foreach (var line in SettingRules.SplitUrlList(value))
                        {
                            if (!SettingRules.IsHttpUrl(line))
                                errors.Add($"Webhook URL '{line}' is not a valid http(s) URL.");
                        }
                        break;
                    default:
                        if (SettingRules.Ints.ContainsKey(key))
                        {
                            if (SettingRules.IntError(key, value) is { } intError)
                                errors.Add(intError);
                        }
                        else if (SettingRules.IsFlag(key))
                        {
                            if (value is not (null or "true" or "false"))
                                errors.Add($"{key} must be 'true' or 'false'.");
                        }
                        else
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
