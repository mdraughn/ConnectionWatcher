namespace ConPersistence;

/// <summary>Settings for the persistence layer (appsettings "Persistence" section).</summary>
public class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>SQLite file path; the directory is created if missing.</summary>
    public string DbPath { get; set; } = "data/conwatch.db";

    /// <summary>Minute rollups older than this are downsampled to hourly.</summary>
    public int MinuteRetentionDays { get; set; } = 90;

    /// <summary>Hourly rollups older than this are downsampled to daily.</summary>
    public int HourlyRetentionMonths { get; set; } = 13;

    /// <summary>UTC hour of day at which the daily retention pass runs.</summary>
    public int RetentionRunHourUtc { get; set; } = 3;

    /// <summary>Delay between a config change and the engine rebuild.</summary>
    public double RebuildDebounceSeconds { get; set; } = 2;
}
