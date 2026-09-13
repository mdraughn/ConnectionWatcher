using Microsoft.EntityFrameworkCore;

namespace ConPersistence;

/// <summary>One structured configuration warning.</summary>
public record HealthWarning(string Code, string Message);

/// <summary>
/// Evaluates whether the current configuration can actually deliver what it
/// promises — chiefly whether the verdict layer has enough anchors to tell
/// internet weather from a single site's incident.
/// </summary>
public class ConfigHealth(IDbContextFactory<ConWatchDbContext> dbFactory, SettingsStore settings)
{
    private const int RecommendedAnchors = 3;

    public async Task<IReadOnlyList<HealthWarning>> EvaluateAsync()
    {
        var warnings = new List<HealthWarning>();
        var anchorPool = await settings.GetAsync(SettingKeys.AnchorPool);
        if (anchorPool is not { Length: > 0 })
        {
            warnings.Add(new HealthWarning("no-anchor-pool",
                "No anchor pool is configured — internet weather detection is off."));
            return warnings;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var pool = await db.Pools.Include(p => p.Members)
            .SingleOrDefaultAsync(p => p.Name == anchorPool);
        if (pool is null)
        {
            warnings.Add(new HealthWarning("anchor-pool-missing",
                $"The verdict anchor pool is set to '{anchorPool}', but no such pool exists."));
            return warnings;
        }

        var threshold = await settings.GetIntAsync(SettingKeys.WeatherThreshold, 2);
        var anchors = pool.Members.Count;
        if (anchors < threshold)
            warnings.Add(new HealthWarning("anchors-below-threshold",
                $"Pool '{anchorPool}' has {anchors} anchor(s) but the weather threshold is {threshold} — " +
                "internet weather detection can never trigger."));
        if (anchors < RecommendedAnchors)
            warnings.Add(new HealthWarning("anchors-below-recommended",
                $"Pool '{anchorPool}' has {anchors} anchor(s); {RecommendedAnchors} or more give a more reliable verdict."));

        // Pool-parent drift: re-parenting the members does not move the pool,
        // and a pool parented above (or without) the members' real parent
        // silently truncates the ISP report's trunk walk. The verdict layer
        // is immune (it walks anchor chains), which makes this easy to miss.
        if (await PoolParentDriftAsync(db, pool) is { } drift)
            warnings.Add(drift);
        return warnings;
    }

    private static async Task<HealthWarning?> PoolParentDriftAsync(ConWatchDbContext db,
        Entities.PoolRow pool)
    {
        var targets = await db.Targets.ToDictionaryAsync(t => t.Id);

        // Each member's ancestor chain, nearest first.
        var chains = new List<List<Guid>>();
        foreach (var member in pool.Members)
        {
            if (!targets.TryGetValue(member.TargetId, out var target))
                continue;
            var chain = new List<Guid>();
            var parentId = target.ParentId;
            for (var depth = 0; depth < 20 && parentId is { } pid; depth++)
            {
                if (!targets.TryGetValue(pid, out var ancestor))
                    break;
                chain.Add(pid);
                parentId = ancestor.ParentId;
            }
            chains.Add(chain);
        }
        if (chains.Count == 0)
            return null;

        // Deepest ancestor shared by every member — where the pool belongs.
        var expected = chains[0].FirstOrDefault(id => chains.All(c => c.Contains(id)));
        if (expected == Guid.Empty || pool.ParentId == expected)
            return null;

        var expectedName = targets[expected].Name;
        var current = pool.ParentId is { } cur && targets.TryGetValue(cur, out var parent)
            ? $"parented to '{parent.Name}'" : "not parented to anything";
        return new HealthWarning("pool-parent-drift",
            $"Pool '{pool.Name}' is {current}, but its members sit behind '{expectedName}' — " +
            $"reports that walk the path from the pool's parent will miss hops. " +
            $"Re-parent the pool to '{expectedName}'.");
    }
}
