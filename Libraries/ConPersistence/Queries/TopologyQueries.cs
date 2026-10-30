using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Queries;

/// <summary>Read-side topology lookups shared by the config health check and the ISP report.</summary>
public static class TopologyQueries
{
    /// <summary>The verdict anchor pool with its members, untracked; null when the name is
    /// null or blank or no such pool exists.</summary>
    public static async Task<PoolRow?> AnchorPoolAsync(ConWatchDbContext db, string? poolName,
        CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(poolName)
            ? null
            : await db.Pools.AsNoTracking().Include(p => p.Members)
                .SingleOrDefaultAsync(p => p.Name == poolName, ct);
}
