using ConPersistence.Config;

namespace ConWatch.Health;

/// <summary>The one place that assembles configuration warnings, so every banner and the
/// dashboard count agree. Pages use <see cref="ForPagesAsync"/> (no network probe);
/// the open <c>/config/health</c> endpoint and the Settings page use <see cref="AllAsync"/>.</summary>
public sealed class HealthReport(ConfigHealth config, CloneHealth clone, WatcherHostService host)
{
    /// <summary>Config warnings plus the engine's refused-TCP warnings. Never probes the network.</summary>
    public async Task<IReadOnlyList<HealthWarning>> ForPagesAsync() =>
        [.. await config.EvaluateAsync(), .. RuntimeHealth.RefusalWarnings(host.Engine)];

    /// <summary>Config warnings, then the clone-source warning (it probes the source node),
    /// then the engine's refused-TCP warnings.</summary>
    public async Task<IReadOnlyList<HealthWarning>> AllAsync()
    {
        var warnings = (await config.EvaluateAsync()).ToList();
        if (await clone.EvaluateAsync() is { } cloneWarning)
            warnings.Add(cloneWarning);
        warnings.AddRange(RuntimeHealth.RefusalWarnings(host.Engine));
        return warnings;
    }
}
