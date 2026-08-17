using System.Threading.Channels;
using ConEngine.Events;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Services;

/// <summary>
/// Persists engine transitions. Events flow through a bounded channel so the
/// engine never blocks on the database; a background loop batches inserts.
/// </summary>
public class TransitionRecorder(IDbContextFactory<ConWatchDbContext> dbFactory,
    ILogger<TransitionRecorder> log) : BackgroundService
{
    private readonly Channel<TargetTransition> _channel = Channel.CreateBounded<TargetTransition>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    private IDisposable? _subscription;

    /// <summary>Subscribes to a transition stream, replacing any prior subscription.</summary>
    public void Attach(IObservable<TargetTransition> source)
    {
        _subscription?.Dispose();
        _subscription = source.Subscribe(
            t =>
            {
                if (!_channel.Writer.TryWrite(t))
                    log.LogWarning("Transition channel full; dropped {Transition}", t);
            },
            ex => log.LogError(ex, "Transition stream error"));
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
                    log.LogError(ex, "Failed writing transitions; retrying in 5s");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: drain what's left without a token.
            try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Final transition flush failed"); }
        }
    }

    /// <summary>Drains everything currently in the channel into the database.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var batch = new List<TargetTransition>();
        while (batch.Count < 500 && _channel.Reader.TryRead(out var t))
            batch.Add(t);
        if (batch.Count == 0)
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Transitions.AddRange(batch.Select(ToRow));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        log.LogDebug("Recorded {Count} transitions", batch.Count);
    }

    private static TransitionRow ToRow(TargetTransition t) => new()
    {
        TimestampUtc = t.Timestamp.UtcDateTime,
        SubjectId = t.TargetId,
        SubjectName = t.TargetName,
        SubjectKind = t.Kind.ToString(),
        FromState = t.From.ToString(),
        ToState = t.To.ToString(),
        Trigger = t.Reason.Trigger.ToString(),
        SuccessRate = t.Reason.SuccessRate,
        SampleSize = t.Reason.SampleSize,
        BurstLossRate = t.Reason.BurstLossRate,
        AncestorId = t.Reason.AncestorId,
        Detail = t.Reason.Detail,
        Verdict = t.Reason.Verdict,
        AvgRtt = t.AverageRoundtripTime,
    };

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
