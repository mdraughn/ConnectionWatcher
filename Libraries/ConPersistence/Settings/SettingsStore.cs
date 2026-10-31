using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Settings;

/// <summary>
/// Typed access to the Settings table. Seeded from appsettings at startup for
/// keys not yet present; the table is authoritative afterwards. Writes to any
/// Verdict.*, Engine.* or Dns.* key (except Dns.RetiredResolvers, which a
/// service writes) raise SettingsChanged (the host debounces an engine rebuild).
/// </summary>
public class SettingsStore(IDbContextFactory<ConWatchDbContext> dbFactory)
{
    /// <summary>Carries the values just written (null = removed), so a listener
    /// need not read them back — the host's master switch is refreshed from it.</summary>
    public event Action<IReadOnlyDictionary<string, string?>>? SettingsChanged;

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

    /// <summary>A boolean flag, read with the default <see cref="SettingRules"/> holds for the key.
    /// Throws <see cref="ArgumentException"/> for a key that is not a flag.</summary>
    public async Task<bool> GetBoolAsync(string key) =>
        SettingRules.ReadFlag(key, await GetAsync(key));

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
                (k.StartsWith("Dns.", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(k, SettingKeys.DnsRetiredResolvers, StringComparison.OrdinalIgnoreCase)) ||
                k.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase)))
            SettingsChanged?.Invoke(values);
        if (values.Keys.Any(k =>
                k.StartsWith("Mqtt.", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("Webhook.", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("Heartbeat.", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase)))
            NotifierSettingsChanged?.Invoke();
    }
}
