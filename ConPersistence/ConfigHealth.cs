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
        return warnings;
    }
}
