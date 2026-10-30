using System.Net;
using ConCore;
using ConPersistence.Entities;
using ConPersistence.Recording;

namespace ConPersistence.Tests;

public class RollupAccumulatorTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("8.8.8.8");
    private static readonly Guid TargetId = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    private static PingResult Ping(DateTime at, bool ok, long rtt) =>
        new(Ip, ok, rtt, timestamp: new DateTimeOffset(at, TimeSpan.Zero));

    [Fact]
    public void FloorUtc_floors_to_the_unit_and_tags_utc()
    {
        var floored = RollupResolution.FloorUtc(
            new DateTime(2026, 10, 29, 13, 47, 59, 999, DateTimeKind.Unspecified), TimeSpan.TicksPerHour);

        Assert.Equal(new DateTime(2026, 10, 29, 13, 0, 0, DateTimeKind.Utc), floored);
        Assert.Equal(DateTimeKind.Utc, floored.Kind);
    }

    [Fact]
    public void Samples_within_a_minute_aggregate()
    {
        var acc = new RollupAccumulator();
        acc.AddSample(TargetId, Ping(T0.AddSeconds(1), true, 10));
        acc.AddSample(TargetId, Ping(T0.AddSeconds(20), true, 30));
        acc.AddSample(TargetId, Ping(T0.AddSeconds(40), false, 0));

        var bucket = Assert.Single(acc.TakeAll());
        Assert.Equal(T0, bucket.PeriodStartUtc);
        Assert.Equal(3, bucket.PingCount);
        Assert.Equal(2, bucket.SuccessCount);
        Assert.Equal(20.0, bucket.RttAvg);
        Assert.Equal(10, bucket.EffectiveMin);
        Assert.Equal(30, bucket.RttMax);
    }

    [Fact]
    public void Samples_spanning_minutes_split_buckets()
    {
        var acc = new RollupAccumulator();
        acc.AddSample(TargetId, Ping(T0.AddSeconds(59), true, 10));
        acc.AddSample(TargetId, Ping(T0.AddSeconds(61), true, 20));
        Assert.Equal(2, acc.TakeAll().Count);
    }

    [Fact]
    public void TakeClosed_returns_only_finished_minutes_and_removes_them()
    {
        var acc = new RollupAccumulator();
        acc.AddSample(TargetId, Ping(T0.AddSeconds(10), true, 10));
        acc.AddSample(TargetId, Ping(T0.AddSeconds(70), true, 20));

        var closed = acc.TakeClosed(T0.AddSeconds(75)); // second minute still open
        var bucket = Assert.Single(closed);
        Assert.Equal(T0, bucket.PeriodStartUtc);

        Assert.Empty(acc.TakeClosed(T0.AddSeconds(76)));      // already taken
        var remaining = Assert.Single(acc.TakeAll());          // open minute still there
        Assert.Equal(T0.AddMinutes(1), remaining.PeriodStartUtc);
    }

    [Fact]
    public void All_failure_bucket_has_zero_rtt_stats()
    {
        var acc = new RollupAccumulator();
        acc.AddSample(TargetId, Ping(T0.AddSeconds(5), false, 0));
        var bucket = Assert.Single(acc.TakeAll());
        Assert.Equal(1, bucket.PingCount);
        Assert.Equal(0, bucket.SuccessCount);
        Assert.Equal(0.0, bucket.RttAvg);
        Assert.Equal(0, bucket.EffectiveMin);
        Assert.Equal(0, bucket.RttMax);
    }

    [Fact]
    public void Multiple_targets_bucket_independently()
    {
        var acc = new RollupAccumulator();
        var other = Guid.NewGuid();
        acc.AddSample(TargetId, Ping(T0.AddSeconds(1), true, 10));
        acc.AddSample(other, Ping(T0.AddSeconds(2), true, 50));
        var buckets = acc.TakeAll();
        Assert.Equal(2, buckets.Count);
        Assert.Contains(buckets, b => b.TargetId == other && b.RttMax == 50);
    }
}
