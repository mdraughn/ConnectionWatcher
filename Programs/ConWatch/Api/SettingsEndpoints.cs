using ConPersistence;
using Microsoft.EntityFrameworkCore;

namespace ConWatch.Api;

/// <summary>Read/write the UI-editable settings (verdict + retention).</summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this RouteGroupBuilder api)
    {
        api.MapGet("/settings", async (SettingsStore store) => await store.GetAllAsync());

        api.MapPut("/settings", async (Dictionary<string, string?> values, SettingsStore store,
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
                    case SettingKeys.RetentionRunHourUtc:
                        if (!int.TryParse(value, out var hour) || hour < 0 || hour > 23)
                            errors.Add("RetentionRunHourUtc must be 0–23.");
                        break;
                    case SettingKeys.RetentionLastRunUtc:
                        errors.Add("Retention.LastRunUtc is read-only.");
                        break;
                    default:
                        errors.Add($"Unknown setting '{key}'.");
                        break;
                }
            }
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            await store.SetManyAsync(values);
            return Results.NoContent();
        });
    }
}
