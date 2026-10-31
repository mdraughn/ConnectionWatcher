using ConCore.Targets;

namespace ConEngine.Scheduling;

/// <summary>
/// Pool phase staggering: spreads the members of each rotation pool across the
/// normal ping interval so they do not all fire at once.
/// </summary>
public static class PoolStagger
{
    /// <summary>Pool membership index for phase staggering: targetId → (memberIndex, memberCount).
    /// A target in several pools takes the first pool by name, so its slot does not depend on
    /// the order pools were loaded in.</summary>
    public static Dictionary<Guid, (int Index, int Count)> Positions(IEnumerable<PoolDefinition> pools)
    {
        var positions = new Dictionary<Guid, (int, int)>();
        foreach (var pool in pools.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            for (int i = 0; i < pool.MemberTargetIds.Count; i++)
                positions.TryAdd(pool.MemberTargetIds[i], (i, pool.MemberTargetIds.Count));
        return positions;
    }

    /// <summary>Stagger offset for member k of n at the given normal interval.</summary>
    public static TimeSpan PhaseOffset(int memberIndex, int memberCount, TimeSpan interval) =>
        interval * memberIndex / memberCount;
}
