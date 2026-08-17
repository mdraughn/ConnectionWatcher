using ConCore;
using ConPersistence.Rollups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Services;

/// <summary>
/// Turns the engine's per-ping sample stream into per-minute rollup rows.
/// Buckets accumulate in memory and are written once their minute closes.
/// </summary>
public class RollupCollector(IDbContextFactory<ConWatchDbContext> dbFactory,
    TimeProvider timeProvider, ILogger<RollupCollector> log) : BackgroundService
{
    private readonly RollupAccumulator _accumulator = new();
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
        catch (OperationCanceledException)
        {
            // Shutdown: flush every open bucket (partial minutes included).
            try { await WriteAsync(_accumulator.TakeAll(), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Final rollup flush failed"); }
        }
    }

    /// <summary>Writes buckets whose minute has closed.</summary>
    public Task FlushClosedAsync(CancellationToken ct = default) =>
        WriteAsync(_accumulator.TakeClosed(timeProvider.GetUtcNow().UtcDateTime), ct);

    /// <summary>Writes every bucket including the open minute (tests / debug endpoint).</summary>
    public Task FlushAllAsync(CancellationToken ct = default) =>
        WriteAsync(_accumulator.TakeAll(), ct);

    private async Task WriteAsync(List<RollupBucket> buckets, CancellationToken ct)
    {
        if (buckets.Count == 0)
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var bucket in buckets)
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
        log.LogDebug("Wrote {Count} rollup buckets", buckets.Count);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
