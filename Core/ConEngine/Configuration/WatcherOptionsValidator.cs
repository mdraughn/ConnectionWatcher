using ConCore;
using Microsoft.Extensions.Options;

namespace ConEngine.Configuration;

/// <summary>Validates the watcher configuration at startup: names, references, tree shape, numbers.</summary>
public class WatcherOptionsValidator : IValidateOptions<WatcherOptions>
{
    public ValidateOptionsResult Validate(string? name, WatcherOptions options)
    {
        var failures = new List<string>();
        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Targets may share an address (each gets its own controller); the
        // same address probed the same way twice is a typo, not a choice.
        var addressProbes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in options.Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Name))
                failures.Add("A target has an empty Name.");
            else if (!targetNames.Add(t.Name))
                failures.Add($"Duplicate target name '{t.Name}'.");

            if (!IsKnownProfile(options, t.Profile))
                failures.Add($"Target '{t.Name}' references unknown profile '{t.Profile}'.");

            var probeValid = ProbeSpec.TryParse(t.Probe, out var probe);
            if (!probeValid)
                failures.Add($"Target '{t.Name}' has an invalid Probe '{t.Probe}' (expected 'tcp:<port>').");
            else if (!ProbePolicy.IsAllowed(t.Address ?? "", probe, out var probeReason))
                failures.Add($"Target '{t.Name}': {probeReason}");

            if (string.IsNullOrWhiteSpace(t.Address))
                failures.Add($"Target '{t.Name}' has an empty Address.");
            else if (probeValid && !addressProbes.Add($"{t.Address}|{probe}"))
                failures.Add($"Duplicate target address '{t.Address}' with probe '{probe}'.");
        }

        var poolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in options.Pools)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
                failures.Add("A pool has an empty Name.");
            else if (!poolNames.Add(p.Name))
                failures.Add($"Duplicate pool name '{p.Name}'.");
            else if (targetNames.Contains(p.Name))
                failures.Add($"Pool name '{p.Name}' collides with a target name.");

            if (p.Members.Count == 0)
                failures.Add($"Pool '{p.Name}' has no members.");
            foreach (var m in p.Members)
            {
                if (!targetNames.Contains(m))
                    failures.Add(options.Pools.Any(x => x.Name.Equals(m, StringComparison.OrdinalIgnoreCase))
                        ? $"Pool '{p.Name}' member '{m}' is a pool; pools cannot contain pools."
                        : $"Pool '{p.Name}' references unknown member '{m}'.");
            }

            if (p.Parent is { Length: > 0 } && !targetNames.Contains(p.Parent))
                failures.Add($"Pool '{p.Name}' references unknown parent target '{p.Parent}'.");
        }

        foreach (var t in options.Targets)
        {
            if (t.Parent is not { Length: > 0 })
                continue;
            if (poolNames.Contains(t.Parent))
                failures.Add($"Target '{t.Name}' has pool '{t.Parent}' as parent; pools cannot be parents.");
            else if (!targetNames.Contains(t.Parent))
                failures.Add($"Target '{t.Name}' references unknown parent '{t.Parent}'.");
        }

        // Cycle detection: walk up from every target.
        var byName = options.Targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var t in options.Targets)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = t;
            while (current?.Parent is { Length: > 0 } parentName)
            {
                if (!seen.Add(current.Name))
                {
                    failures.Add($"Parent cycle detected involving target '{t.Name}'.");
                    break;
                }
                byName.TryGetValue(parentName, out current);
            }
        }

        foreach (var (profileName, p) in options.Profiles)
        {
            if (p.NormalIntervalSeconds <= 0 || p.BurstIntervalSeconds <= 0 ||
                p.BurstMaxDurationSeconds <= 0 || p.PingTimeoutSeconds <= 0)
                failures.Add($"Profile '{profileName}': intervals and timeout must be positive.");
            foreach (var (label, v) in new (string, double)[]
                     {
                         ("RespondingThreshold", p.RespondingThreshold),
                         ("FalteringThreshold", p.FalteringThreshold),
                         ("DownThreshold", p.DownThreshold),
                         ("RecoveringThreshold", p.RecoveringThreshold),
                     })
            {
                if (v is < 0 or > 1)
                    failures.Add($"Profile '{profileName}': {label} must be between 0 and 1.");
            }
            if (p.MinUsableSample < 1 || p.MinUsableSample > p.HistorySize)
                failures.Add($"Profile '{profileName}': MinUsableSample must be between 1 and HistorySize ({p.HistorySize}).");
            if (p.FalteringThreshold >= p.RespondingThreshold)
                failures.Add($"Profile '{profileName}': FalteringThreshold ({p.FalteringThreshold}) must be below RespondingThreshold ({p.RespondingThreshold}), or the state flaps on every sample between them.");
            if (p.DownThreshold >= p.RecoveringThreshold)
                failures.Add($"Profile '{profileName}': DownThreshold ({p.DownThreshold}) must be below RecoveringThreshold ({p.RecoveringThreshold}).");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static bool IsKnownProfile(WatcherOptions options, string profileName) =>
        ConCore.Targets.PingProfile.IsBuiltIn(profileName) ||
        options.Profiles.ContainsKey(profileName);
}
