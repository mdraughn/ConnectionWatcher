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
    public const string RetentionRunHourUtc = "Retention.RetentionRunHourUtc";
    public const string RetentionLastRunUtc = "Retention.LastRunUtc";
}

/// <summary>
/// Typed access to the Settings table. Seeded from appsettings at startup for
/// keys not yet present; the table is authoritative afterwards. Writes to any
/// Verdict.* key raise SettingsChanged (the host debounces an engine rebuild).
/// </summary>
public class SettingsStore(IDbContextFactory<ConWatchDbContext> dbFactory)
{
    public event Action? SettingsChanged;

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

        if (values.Keys.Any(k => k.StartsWith("Verdict.", StringComparison.OrdinalIgnoreCase)))
            SettingsChanged?.Invoke();
    }
}
