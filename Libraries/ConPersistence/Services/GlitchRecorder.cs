using ConEngine.Events;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Services;

/// <summary>
/// Persists glitch events (see <see cref="ChannelRecorder{TEvent}"/> for the
/// mechanics). A Coalesced event extends the target's most recent glitch row
/// instead of inserting — one ongoing rough patch stays one row — which is
/// why batches must stay whole.
/// </summary>
public class GlitchRecorder(IDbContextFactory<ConWatchDbContext> dbFactory,
    ILogger<GlitchRecorder> log) : ChannelRecorder<GlitchEvent>(log)
{
    protected override string EventNoun => "glitches";

    protected override async Task WriteBatchAsync(IReadOnlyList<GlitchEvent> batch, CancellationToken ct)
    {
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
    }
}
