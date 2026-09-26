using ConEngine;
using ConEngine.Configuration;
using ConEngine.Topology;
using ConEngine.Tracking;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Queries;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;

namespace ConWatch;

/// <summary>
/// Warm-start policy: which presumed states a new engine may start from.
/// Pure policy over the transition log, the stored config and one setting;
/// the lifecycle (when to seed, under which lock) stays in WatcherHostService.
/// Spec: docs/superpowers/specs/2026-09-11-warm-restart-design.md.
/// </summary>
public sealed class WarmStartSeeder(IDbContextFactory<ConWatchDbContext> dbFactory, ConfigStore store,
    SettingsStore settingsStore, TimeProvider timeProvider, ILogger<WarmStartSeeder> log)
{
    /// <summary>False when Engine.WarmRestartMaxGapMinutes is 0 or less: no
    /// seeding at all, neither at process start nor on a config rebuild.</summary>
    public async Task<bool> EnabledAsync() => await MaxGapMinutesAsync() > 0;

    private Task<int> MaxGapMinutesAsync() =>
        settingsStore.GetIntAsync(SettingKeys.WarmRestartMaxGapMinutes, SettingsDefaults.WarmRestartMaxGapMinutes);

    /// <summary>
    /// Presumed states from the last recorded transitions, gated by freshness:
    /// only when the watcher was last alive within WarmRestartMaxGapMinutes.
    /// HistoryReset rows (repointed targets) and undeterminable states never
    /// seed; an Unreachable seed needs an ancestor seeded Down to justify it.
    /// </summary>
    public async Task<EngineSeed?> FromDbAsync()
    {
        var maxGapMinutes = await MaxGapMinutesAsync();
        if (maxGapMinutes <= 0)
            return null;

        await using var db = await dbFactory.CreateDbContextAsync();
        var lastAlive = await WarmStartQueries.LastAliveUtcAsync(db);
        if (lastAlive is null)
            return null;
        var gap = timeProvider.GetUtcNow().UtcDateTime - lastAlive.Value;
        if (gap > TimeSpan.FromMinutes(maxGapMinutes))
        {
            log.LogInformation(
                "Cold start: watcher was last alive {Gap} ago (warm-restart window is {Max} min)",
                gap, maxGapMinutes);
            return null;
        }

        var options = await store.LoadWatcherOptionsAsync();
        var targetsById = options.Targets.Where(t => t.Id is not null)
            .ToDictionary(t => t.Id!.Value);
        var latest = await WarmStartQueries.LatestTransitionsPerSubjectAsync(db);

        var seed = new EngineSeed();
        foreach (var row in latest)
        {
            if (row.Trigger == "HistoryReset")
                continue;
            switch (row.SubjectKind)
            {
                case "Target" when targetsById.TryGetValue(row.SubjectId, out var target)
                    && Enum.TryParse<HostState>(row.ToState, out var state)
                    && state is not (HostState.Initializing or HostState.Starting):
                    seed.Targets[row.SubjectId] = new TargetSeed(state, target.Address, target.Probe);
                    break;
                case "Pool" when Enum.TryParse<HostState>(row.ToState, out var poolState)
                    && poolState is not (HostState.Initializing or HostState.Starting):
                    seed.Pools[row.SubjectId] = poolState;
                    break;
            }
        }
        var verdictRow = latest.FirstOrDefault(r => r.SubjectKind == "Verdict"
            && r.Trigger != "HistoryReset");
        if (verdictRow?.Verdict is { } verdictName &&
            Enum.TryParse<VerdictState>(verdictName, out var verdict) &&
            verdict != VerdictState.Unknown)
        {
            seed = new EngineSeed { Targets = seed.Targets, Pools = seed.Pools, Verdict = verdict };
        }

        DropUnjustifiedUnreachable(seed, targetsById);
        if (seed.IsEmpty)
            return null;
        log.LogInformation(
            "Warm restart: last alive {Gap} ago; seeding {Targets} target(s), {Pools} pool(s), verdict {Verdict}",
            gap, seed.Targets.Count, seed.Pools.Count, seed.Verdict?.ToString() ?? "unseeded");
        return seed;
    }

    /// <summary>An Unreachable presumption is only coherent behind a Down
    /// ancestor; without one, that target cold-starts instead. Config links
    /// parents by name, so the walk goes name-by-name (depth-capped).</summary>
    private static void DropUnjustifiedUnreachable(EngineSeed seed,
        IReadOnlyDictionary<Guid, TargetConfig> targetsById)
    {
        var byName = new Dictionary<string, (Guid Id, TargetConfig Config)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (id, config) in targetsById)
            byName[config.Name] = (id, config);

        foreach (var (id, targetSeed) in seed.Targets.ToList())
        {
            if (targetSeed.State != HostState.Unreachable)
                continue;
            var justified = false;
            var parentName = targetsById.TryGetValue(id, out var t) ? t.Parent : null;
            for (var depth = 0; depth < 20 && parentName is { Length: > 0 }; depth++)
            {
                if (!byName.TryGetValue(parentName, out var ancestor))
                    break;
                if (seed.Targets.TryGetValue(ancestor.Id, out var ancestorSeed) &&
                    ancestorSeed.State == HostState.Down)
                {
                    justified = true;
                    break;
                }
                parentName = ancestor.Config.Parent;
            }
            if (!justified)
                seed.Targets.Remove(id);
        }
    }

    /// <summary>Presumed states carried over from the outgoing engine on a
    /// config rebuild (fresh by definition — no gap gate). Addresses ride
    /// along so a repointed target cold-starts.</summary>
    public static EngineSeed FromEngine(WatcherEngine engine)
    {
        var seed = new EngineSeed
        {
            Verdict = engine.Verdict is { CurrentVerdict: not VerdictState.Unknown } v
                ? v.CurrentVerdict : null,
        };
        foreach (var (id, monitor) in engine.Monitors)
        {
            if (monitor.CurrentState is HostState.Initializing or HostState.Starting)
                continue;
            seed.Targets[id] = new TargetSeed(monitor.CurrentState, monitor.Target.Address, monitor.Target.Probe);
        }
        foreach (var pool in engine.PoolCoordinators)
        {
            if (pool.CurrentState is not (HostState.Initializing or HostState.Starting))
                seed.Pools[pool.Pool.Id] = pool.CurrentState;
        }
        return seed;
    }
}
