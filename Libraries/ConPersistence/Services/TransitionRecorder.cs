using ConEngine.Events;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Services;

/// <summary>Persists engine transitions (see <see cref="ChannelRecorder{TEvent}"/> for the mechanics).</summary>
public class TransitionRecorder(IDbContextFactory<ConWatchDbContext> dbFactory,
    ILogger<TransitionRecorder> log) : ChannelRecorder<TargetTransition>(log)
{
    protected override string EventNoun => "transitions";

    protected override async Task WriteBatchAsync(IReadOnlyList<TargetTransition> batch, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Transitions.AddRange(batch.Select(ToRow));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
}
