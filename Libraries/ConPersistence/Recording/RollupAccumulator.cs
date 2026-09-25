using ConCore;

namespace ConPersistence.Recording;

/// <summary>One in-progress minute of ping statistics for one target.</summary>
public class RollupBucket(Guid targetId, DateTime periodStartUtc)
{
    public Guid TargetId { get; } = targetId;
    public DateTime PeriodStartUtc { get; } = periodStartUtc;
    public int PingCount { get; set; }
    public int SuccessCount { get; set; }
    public long RttSum { get; set; }
    public long RttMin { get; set; } = long.MaxValue;
    public long RttMax { get; set; }

    public double RttAvg => SuccessCount > 0 ? (double)RttSum / SuccessCount : 0;
    /// <summary>RttMin, or 0 when the bucket has no successful pings.</summary>
    public long EffectiveMin => SuccessCount > 0 ? RttMin : 0;

    /// <summary>Folds another bucket for the same target and minute into this one.</summary>
    public void Absorb(RollupBucket other)
    {
        PingCount += other.PingCount;
        if (other.SuccessCount > 0)
        {
            RttSum += other.RttSum;
            RttMin = Math.Min(RttMin, other.RttMin);
            RttMax = Math.Max(RttMax, other.RttMax);
            SuccessCount += other.SuccessCount;
        }
    }
}

/// <summary>
/// Accumulates ping samples into per-target minute buckets. RTT statistics
/// count successful pings only; failures contribute to PingCount alone.
/// Thread-safe: samples arrive from the engine's Rx callbacks.
/// </summary>
public class RollupAccumulator
{
    private readonly Dictionary<(Guid TargetId, DateTime Minute), RollupBucket> _open = new();
    private readonly Lock _lock = new();

    public void AddSample(Guid targetId, PingResult result)
    {
        var utc = result.Timestamp.UtcDateTime;
        var minute = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        lock (_lock)
        {
            if (!_open.TryGetValue((targetId, minute), out var bucket))
                _open[(targetId, minute)] = bucket = new RollupBucket(targetId, minute);

            bucket.PingCount++;
            if (result.Success)
            {
                bucket.SuccessCount++;
                bucket.RttSum += result.RoundtripTime;
                bucket.RttMin = Math.Min(bucket.RttMin, result.RoundtripTime);
                bucket.RttMax = Math.Max(bucket.RttMax, result.RoundtripTime);
            }
        }
    }

    /// <summary>Removes and returns buckets whose minute has fully elapsed.</summary>
    public List<RollupBucket> TakeClosed(DateTime nowUtc)
    {
        var currentMinute = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        lock (_lock)
        {
            var closed = _open.Values.Where(b => b.PeriodStartUtc < currentMinute).ToList();
            foreach (var b in closed)
                _open.Remove((b.TargetId, b.PeriodStartUtc));
            return closed;
        }
    }

    /// <summary>Removes and returns every bucket (shutdown flush).</summary>
    public List<RollupBucket> TakeAll()
    {
        lock (_lock)
        {
            var all = _open.Values.ToList();
            _open.Clear();
            return all;
        }
    }
}
