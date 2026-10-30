using ConPersistence.Settings;

namespace ConPersistence.Retention;

/// <summary>
/// The three ages the retention pass applies. The pass reads nothing else
/// from configuration, so a new PersistenceOptions field can no longer be
/// silently reset to its default by a pass that rebuilt the whole options object.
/// </summary>
public sealed record RetentionWindows(int MinuteDays, int HourlyMonths, int TransitionMonths)
{
    public static RetentionWindows From(PersistenceOptions options) =>
        new(options.MinuteRetentionDays, options.HourlyRetentionMonths, options.TransitionRetentionMonths);

    /// <summary>The Settings table over appsettings: the windows retention applies and the
    /// History chart's auto resolution reads, in one settings read.</summary>
    public static async Task<RetentionWindows> EffectiveAsync(SettingsStore settings, PersistenceOptions options) =>
        FromSettings(await settings.GetAllAsync().ConfigureAwait(false), options);

    internal static RetentionWindows FromSettings(IReadOnlyDictionary<string, string> all, PersistenceOptions o)
    {
        return new RetentionWindows(
            SettingRules.IntOr(all, SettingKeys.MinuteRetentionDays, o.MinuteRetentionDays),
            SettingRules.IntOr(all, SettingKeys.HourlyRetentionMonths, o.HourlyRetentionMonths),
            SettingRules.IntOr(all, SettingKeys.TransitionRetentionMonths, o.TransitionRetentionMonths));
    }
}
