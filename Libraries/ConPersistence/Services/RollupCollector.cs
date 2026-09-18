using ConCore;
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
    TimeProvider timeProvider, ILogger<RollupCollector> log) : BackgroundService
{
    private readonly RollupAccumulator _accumulator = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private List<RollupBucket> _pending = new();
    private IDisposable? _subscription;

    /// <summary>Subscribes to a ping-sample stream, replacing any prior subscription.</summary>
    public void Attach(IObservable<(Guid TargetId, PingResult Result)> source)
    {
        _subscription?.Dispose();
        _subscription = source.Subscribe(
            s => _accumulator.AddSample(s.TargetId, s.Result),
            ex => log.LogError(ex, "Ping sample stream error"));
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
                catch (Exception ex) when (ex is not OperationCanceledException)
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
        WriteAsync(_accumulator.TakeClosed(timeProvider.GetUtcNow().UtcDateTime), ct);

    /// <summary>Writes every bucket including the open minute (tests / take-over final sync).</summary>
    public Task FlushAllAsync(CancellationToken ct = default) =>
        WriteAsync(_accumulator.TakeAll(), ct);

    /// <summary>
    /// Serialized: the timer loop and a caller-driven flush (take-over, tests)
    /// must not interleave. Two buckets for the same target and minute can
    /// sit in the pending list after a failure; the upsert merges the second
    /// into the row the first created, so nothing is counted twice.
    /// </summary>
    private async Task WriteAsync(List<RollupBucket> buckets, CancellationToken ct)
    {
        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _pending.AddRange(buckets);
            if (_pending.Count == 0)
                return;

            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            foreach (var bucket in _pending)
            {
                var row = RollupMerge.ToRow(bucket);
                var existing = await db.Rollups.SingleOrDefaultAsync(r =>
                    r.TargetId == row.TargetId && r.Resolution == row.Resolution &&
                    r.PeriodStartUtc == row.PeriodStartUtc, ct).ConfigureAwait(false);
                if (existing is null)
                    db.Rollups.Add(row);
                else
                    RollupMerge.Combine(existing, row);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            log.LogDebug("Wrote {Count} rollup buckets", _pending.Count);
            _pending = new();
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
