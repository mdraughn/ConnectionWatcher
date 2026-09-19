using ConCore;
using ConEngine;
using ConPersistence.Rollups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Services;

/// <summary>
/// Turns the engine's per-ping sample stream into per-minute rollup rows.
/// Buckets accumulate in memory and are written once their minute closes.
/// Buckets taken out of the accumulator stay pending until their write
/// succeeds, so a transient database error never drops a minute.
/// </summary>
public class RollupCollector(IDbContextFactory<ConWatchDbContext> dbFactory,
    TimeProvider timeProvider, ILogger<RollupCollector> log,
    int maxConsecutiveFailures = 20) : BackgroundService, IEngineObserver
{
    private readonly RollupAccumulator _accumulator = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private List<RollupBucket> _pending = new();
    private int _consecutiveFailures;
    private IDisposable? _subscription;

    /// <summary>Subscribes to a ping-sample stream, replacing any prior subscription.</summary>
    public void Attach(IObservable<(Guid TargetId, PingResult Result)> source)
    {
        _subscription?.Dispose();
        _subscription = source.Subscribe(
            s => _accumulator.AddSample(s.TargetId, s.Result),
            ex => log.LogError(ex, "Ping sample stream error"));
    }

    public void Attach(WatcherEngine engine) => Attach(engine.PingSampleSource);

    public void Detach()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), timeProvider, stoppingToken).ConfigureAwait(false);
                try
                {
                    await FlushClosedAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Failed writing rollups; will retry next cycle");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: flush every open bucket (partial minutes included).
            try { await FlushAllAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Final rollup flush failed"); }
        }
    }

    /// <summary>Writes buckets whose minute has closed.</summary>
    public Task FlushClosedAsync(CancellationToken ct = default) =>
        WriteAsync(() => _accumulator.TakeClosed(timeProvider.GetUtcNow().UtcDateTime), ct);

    /// <summary>Writes every bucket including the open minute (tests / take-over final sync).</summary>
    public Task FlushAllAsync(CancellationToken ct = default) =>
        WriteAsync(() => _accumulator.TakeAll(), ct);

    /// <summary>
    /// Serialized: the timer loop and a caller-driven flush (take-over, tests)
    /// must not interleave. Buckets are taken from the accumulator only once
    /// the lock is held, so a cancelled wait cannot strand them. Pending
    /// buckets for the same target and minute (a late sample after a failed
    /// write) are folded into the pending list itself before the write, so a
    /// failed write leaves the already-folded list and a retry can never
    /// absorb the same duplicate twice or collide with itself on the unique
    /// index. After too many consecutive failures the pending list is
    /// dropped with an error rather than growing forever.
    /// </summary>
    private async Task WriteAsync(Func<List<RollupBucket>> take, CancellationToken ct)
    {
        await _flushLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _pending.AddRange(take());
            if (_pending.Count == 0)
                return;
            ct.ThrowIfCancellationRequested();

            // Fold duplicates into the pending list itself: a failed write then
            // leaves the folded list, and nothing is ever absorbed twice.
            _pending = Coalesce(_pending);
            var merged = _pending;
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var earliest = merged.Min(b => b.PeriodStartUtc);
            var targetIds = merged.Select(b => b.TargetId).Distinct().ToList();
            var existing = await db.Rollups
                .Where(r => r.Resolution == "Minute" && r.PeriodStartUtc >= earliest && targetIds.Contains(r.TargetId))
                .ToListAsync(ct).ConfigureAwait(false);
            var byKey = existing.ToDictionary(r => (r.TargetId, r.PeriodStartUtc));
            foreach (var bucket in merged)
            {
                var row = RollupMerge.ToRow(bucket);
                if (byKey.TryGetValue((row.TargetId, row.PeriodStartUtc), out var found))
                    RollupMerge.Combine(found, row);
                else
                    db.Rollups.Add(row);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            log.LogDebug("Wrote {Count} rollup buckets", merged.Count);
            _pending = new();
            _consecutiveFailures = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (++_consecutiveFailures >= maxConsecutiveFailures)
            {
                log.LogError(ex, "Dropping {Count} pending rollup buckets after {Failures} consecutive failed writes",
                    _pending.Count, _consecutiveFailures);
                _pending = new();
                _consecutiveFailures = 0;
            }
            throw;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private static List<RollupBucket> Coalesce(List<RollupBucket> buckets)
    {
        var byKey = new Dictionary<(Guid, DateTime), RollupBucket>();
        foreach (var bucket in buckets)
        {
            if (byKey.TryGetValue((bucket.TargetId, bucket.PeriodStartUtc), out var first))
                first.Absorb(bucket);
            else
                byKey[(bucket.TargetId, bucket.PeriodStartUtc)] = bucket;
        }
        return byKey.Values.ToList();
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
