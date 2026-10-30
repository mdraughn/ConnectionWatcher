namespace ConPersistence.Entities;

/// <summary>The stored values of a rollup row's Resolution column.</summary>
public static class RollupResolution
{
    public const string Minute = "Minute";
    public const string Hour = "Hour";
    public const string Day = "Day";

    /// <summary>Floors <paramref name="t"/> to a multiple of <paramref name="unitTicks"/>, tagged UTC.</summary>
    public static DateTime FloorUtc(DateTime t, long unitTicks) =>
        new(t.Ticks - t.Ticks % unitTicks, DateTimeKind.Utc);
}
