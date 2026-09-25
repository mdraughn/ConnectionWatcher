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
}
