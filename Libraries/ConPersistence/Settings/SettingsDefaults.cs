using System.Globalization;
using ConCore.Dns;
using ConEngine.Configuration;

namespace ConPersistence.Settings;

/// <summary>
/// The settings a node starts with. First boot seeds them for keys not yet in
/// the table (the upgrade path); factory reset seeds the same list. Values
/// come from appsettings; the table is authoritative afterwards.
/// </summary>
public static class SettingsDefaults
{
    /// <summary>Warm restart seeds only when the watcher was last alive within this many minutes.</summary>
    public const int WarmRestartMaxGapMinutes = 10;

    /// <summary>The compiled-in weather threshold: the fallback where the setting cannot be read.</summary>
    public static readonly int WeatherThreshold = new VerdictOptions().WeatherThreshold;

    public static Dictionary<string, string> For(VerdictOptions verdict, PersistenceOptions persistence)
    {
        var defaults = new Dictionary<string, string>
        {
            [SettingKeys.WeatherThreshold] = Invariant(verdict.WeatherThreshold),
            [SettingKeys.MinuteRetentionDays] = Invariant(persistence.MinuteRetentionDays),
            [SettingKeys.HourlyRetentionMonths] = Invariant(persistence.HourlyRetentionMonths),
            [SettingKeys.TransitionRetentionMonths] = Invariant(persistence.TransitionRetentionMonths),
            [SettingKeys.RetentionRunHourUtc] = Invariant(persistence.RetentionRunHourUtc),
            [SettingKeys.WarmRestartMaxGapMinutes] = Invariant(WarmRestartMaxGapMinutes),
            [SettingKeys.DnsEnabled] = "true",
            [SettingKeys.DnsLookupNames] = DnsLookupName.ToJson(DnsLookupName.Seeded),
        };
        if (verdict.AnchorPool is { Length: > 0 } anchorPool)
            defaults[SettingKeys.AnchorPool] = anchorPool;
        return defaults;
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
