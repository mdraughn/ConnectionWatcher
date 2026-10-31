using ConEngine;
using ConEngine.Topology;
using ConPersistence.Entities;
using ConPersistence.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Warnings;

/// <summary>A model-warning instance as the ledger holds it.</summary>
public sealed record ModelWarningInstance(string Key, ModelWarningKind Kind, Guid? TargetId,
    string? TargetName, string Message, DateTimeOffset SinceUtc, bool Acknowledged);

public enum ModelWarningOpKind { Opened, Closed, Acknowledged }

/// <summary>One lifecycle step of an instance, in ledger order.</summary>
public sealed record ModelWarningOp(ModelWarningOpKind Op, ModelWarningInstance Warning, DateTimeOffset AtUtc);

/// <summary>
/// Persists the ledger's lifecycle ops (see <see cref="ChannelRecorder{TEvent}"/>
/// for the mechanics). Fed by <see cref="ModelWarningLedger"/>, never attached
/// to an engine directly: the ledger decides what is a new instance.
/// </summary>
public class ModelWarningRecorder(IDbContextFactory<ConWatchDbContext> dbFactory,
    ILogger<ModelWarningRecorder> log, TimeSpan? retryDelay = null)
    : ChannelRecorder<ModelWarningOp>(log, retryDelay: retryDelay)
{
    protected override string EventNoun => "model warnings";

    protected override async Task WriteBatchAsync(IReadOnlyList<ModelWarningOp> batch, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // The open row may still be unsaved from this same batch.
        var openedThisBatch = new Dictionary<string, ModelWarningRow>();
        foreach (var op in batch)
        {
            var w = op.Warning;
            if (op.Op == ModelWarningOpKind.Opened)
            {
                var row = new ModelWarningRow
                {
                    Key = w.Key,
                    Kind = w.Kind.ToString(),
                    TargetId = w.TargetId,
                    TargetName = w.TargetName,
                    Message = w.Message,
                    StartUtc = w.SinceUtc.UtcDateTime,
                };
                db.ModelWarnings.Add(row);
                openedThisBatch[w.Key] = row;
                continue;
            }

            var open = openedThisBatch.GetValueOrDefault(w.Key)
                ?? await db.ModelWarnings
                    .Where(r => r.Key == w.Key && r.EndUtc == null)
                    .OrderByDescending(r => r.StartUtc)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (open is null)
                continue; // history was cleared under an open instance
            if (op.Op == ModelWarningOpKind.Closed)
            {
                open.EndUtc = op.AtUtc.UtcDateTime;
                openedThisBatch.Remove(w.Key);
            }
            else
            {
                open.AcknowledgedUtc = op.AtUtc.UtcDateTime;
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
