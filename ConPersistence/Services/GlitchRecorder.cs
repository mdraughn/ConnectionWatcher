using System.Threading.Channels;
using ConEngine.Events;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Services;

/// <summary>
/// Persists glitch events (same channel-batching shape as TransitionRecorder).
/// A Coalesced event extends the target's most recent glitch row instead of
/// inserting — one ongoing rough patch stays one row.
/// </summary>
public class GlitchRecorder(IDbContextFactory<ConWatchDbContext> dbFactory,
    ILogger<GlitchRecorder> log) : BackgroundService
{
    private readonly Channel<GlitchEvent> _channel = Channel.CreateBounded<GlitchEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    private IDisposable? _subscription;

    /// <summary>Subscribes to a glitch stream, replacing any prior subscription.</summary>
    public void Attach(IObservable<GlitchEvent> source)
    {
        _subscription?.Dispose();
        _subscription = source.Subscribe(
            g =>
            {
                if (!_channel.Writer.TryWrite(g))
                    log.LogWarning("Glitch channel full; dropped {Glitch}", g);
            },
            ex => log.LogError(ex, "Glitch stream error"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Failed writing glitches; retrying in 5s");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Final glitch flush failed"); }
        }
    }

    /// <summary>Drains everything currently in the channel into the database.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var batch = new List<GlitchEvent>();
        while (batch.Count < 500 && _channel.Reader.TryRead(out var g))
            batch.Add(g);
        if (batch.Count == 0)
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var addedThisBatch = new Dictionary<Guid, GlitchRow>();
        foreach (var g in batch)
        {
            GlitchRow? extend = null;
            if (g.Coalesced)
            {
                // The row to extend may still be unsaved from this same batch.
                extend = addedThisBatch.GetValueOrDefault(g.TargetId)
                    ?? await db.Glitches
                        .Where(r => r.TargetId == g.TargetId)
                        .OrderByDescending(r => r.StartUtc)
                        .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            }
            if (extend is not null)
            {
                extend.EndUtc = g.EndUtc.UtcDateTime;
                extend.LostCount += g.LostCount;
                extend.SampleCount += g.SampleCount;
            }
            else
            {
                var row = new GlitchRow
                {
                    TargetId = g.TargetId,
                    TargetName = g.TargetName,
                    Class = (int)g.Class,
                    StartUtc = g.StartUtc.UtcDateTime,
                    EndUtc = g.EndUtc.UtcDateTime,
                    LostCount = g.LostCount,
                    SampleCount = g.SampleCount,
                    Detail = g.Detail,
                };
                db.Glitches.Add(row);
                addedThisBatch[g.TargetId] = row;
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        log.LogDebug("Recorded {Count} glitches", batch.Count);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
