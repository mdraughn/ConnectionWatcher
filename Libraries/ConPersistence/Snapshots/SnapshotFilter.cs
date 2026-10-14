using ConPersistence.Config;

namespace ConPersistence.Snapshots;

/// <summary>Drops targets from a <see cref="Snapshot"/> together with everything recorded for them.</summary>
public static class SnapshotFilter
{
    /// <summary>The snapshot minus the targets matching <paramref name="drop"/> and the
    /// transitions, rollups and glitches recorded for them. Pools and the verdict cannot
    /// reference such targets (validator rule), so nothing else needs rewriting.</summary>
    public static Snapshot Without(Snapshot snapshot, Func<TargetDto, bool> drop)
    {
        var droppedIds = snapshot.Config.Targets
            .Where(t => drop(t) && t.Id is not null)
            .Select(t => t.Id!.Value).ToHashSet();
        return snapshot with
        {
            Config = snapshot.Config with
            {
                Targets = snapshot.Config.Targets.Where(t => !drop(t)).ToList(),
            },
            Transitions = snapshot.Transitions.Where(t => !droppedIds.Contains(t.SubjectId)).ToList(),
            Rollups = snapshot.Rollups.Where(r => !droppedIds.Contains(r.TargetId)).ToList(),
            Glitches = snapshot.Glitches.Where(g => !droppedIds.Contains(g.TargetId)).ToList(),
        };
    }

    /// <summary>System resolvers are the SOURCE host's; the destination's
    /// SystemResolverService regenerates its own (spec 2026-10-10 §5.1).</summary>
    public static Snapshot WithoutSystemManaged(Snapshot snapshot) => Without(snapshot, t => t.SystemManaged);
}
