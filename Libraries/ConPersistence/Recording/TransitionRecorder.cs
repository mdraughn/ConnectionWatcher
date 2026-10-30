using ConEngine;
using ConEngine.Events;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Recording;

/// <summary>Persists engine transitions (see <see cref="ChannelRecorder{TEvent}"/> for the mechanics).</summary>
public class TransitionRecorder(IDbContextFactory<ConWatchDbContext> dbFactory,
    ILogger<TransitionRecorder> log, TimeSpan? retryDelay = null, int capacity = 1000)
    : ChannelRecorder<TargetTransition>(log, capacity, retryDelay: retryDelay)
{
    protected override string EventNoun => "transitions";

    protected override IObservable<TargetTransition> SourceOf(WatcherEngine engine) => engine.TransitionSource;

    protected override async Task WriteBatchAsync(IReadOnlyList<TargetTransition> batch, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Transitions.AddRange(batch.Select(TransitionRow.From));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
