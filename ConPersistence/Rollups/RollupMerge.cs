using ConPersistence.Entities;

namespace ConPersistence.Rollups;

/// <summary>
/// Merges rollup rows covering the same subject/period: counts sum, min/max
/// combine, averages are weighted by success count. Rows with zero successes
/// contribute nothing to the RTT statistics.
/// </summary>
public static class RollupMerge
{
    public static void Combine(RollupRow target, RollupRow addition)
    {
        var totalSuccess = target.SuccessCount + addition.SuccessCount;
        if (totalSuccess > 0)
        {
            target.RttAvg = (target.RttAvg * target.SuccessCount + addition.RttAvg * addition.SuccessCount)
                / totalSuccess;
            target.RttMin = (target.SuccessCount, addition.SuccessCount) switch
            {
                (0, _) => addition.RttMin,
                (_, 0) => target.RttMin,
                _ => Math.Min(target.RttMin, addition.RttMin),
            };
            target.RttMax = Math.Max(target.RttMax, addition.RttMax);
        }
        target.PingCount += addition.PingCount;
        target.SuccessCount = totalSuccess;
    }

    public static RollupRow ToRow(RollupBucket bucket, string resolution = "Minute") => new()
    {
        TargetId = bucket.TargetId,
        PeriodStartUtc = bucket.PeriodStartUtc,
        Resolution = resolution,
        PingCount = bucket.PingCount,
        SuccessCount = bucket.SuccessCount,
        RttAvg = bucket.RttAvg,
        RttMin = bucket.EffectiveMin,
        RttMax = bucket.RttMax,
    };
}
