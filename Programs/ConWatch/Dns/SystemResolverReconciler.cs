using ConCore.Targets;
using ConInfrastructure.NameResolution;
using ConPersistence.Config;

namespace ConWatch.NameResolution;

/// <summary>
/// Decides what the stored targets should look like given the host's OS
/// resolver list (spec 2026-10-10 §4.3, D7). Pure: the service applies the
/// result. A system-managed target is created when its address appears,
/// disabled (never deleted — history stays) when it disappears or the watch
/// is off, and re-enabled when it returns only if this reconciler disabled it;
/// a target the admin disabled stays disabled.
/// </summary>
public static class SystemResolverReconciler
{
    public const string ProfileName = "Resolver";

    public sealed record Plan(IReadOnlyList<TargetDto> Targets, IReadOnlySet<string> Retired, bool Changed, IReadOnlyList<string> Log);

    public static string NameFor(SystemResolver resolver) =>
        resolver.IsDockerEmbedded ? "Docker's resolver" : $"Your resolver {resolver.Address}";

    public static Plan Reconcile(IReadOnlyList<TargetDto> targets, IReadOnlyList<SystemResolver> found,
        bool watchActive, IReadOnlySet<string> retired)
    {
        var retiredNow = new HashSet<string>(retired, StringComparer.OrdinalIgnoreCase);
        var result = targets.ToList();
        var log = new List<string>();
        var changed = false;

        var foundByAddress = found
            .GroupBy(f => f.Address.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Present addresses: create, or re-enable what we retired.
        foreach (var (address, resolver) in foundByAddress)
        {
            var index = result.FindIndex(t => t.SystemManaged && string.Equals(t.Address, address, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                if (!watchActive)
                    continue;
                if (result.Any(t => !t.SystemManaged
                        && string.Equals(t.Probe, "dns", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.Address, address, StringComparison.OrdinalIgnoreCase)))
                {
                    // The validator rejects two targets with the same address and probe; a hand-made one wins.
                    log.Add($"skipped {address}: a target with a dns probe already exists at that address");
                    continue;
                }
                var name = UniqueName(NameFor(resolver), result);
                result.Add(new TargetDto
                {
                    Name = name, Address = address, Profile = ProfileName, Probe = "dns",
                    Scope = TargetScope.Resolver, SystemManaged = true, Enabled = true,
                });
                log.Add($"added {name}");
                changed = true;
            }
            else if (!result[index].Enabled && watchActive && retiredNow.Remove(address))
            {
                result[index] = result[index] with { Enabled = true };
                log.Add($"re-enabled {result[index].Name}");
                changed = true;
            }
            else if (result[index].Enabled && watchActive && retiredNow.Remove(address))
            {
                // Enabled by hand (or otherwise): the retired set only holds what we keep disabled.
                log.Add($"forgot retired {result[index].Name}");
                changed = true;
            }
        }

        // Managed targets whose address is gone, or the watch is off: retire.
        for (var i = 0; i < result.Count; i++)
        {
            var t = result[i];
            if (!t.SystemManaged || !t.Enabled)
                continue;
            if (watchActive && foundByAddress.ContainsKey(t.Address))
                continue;
            result[i] = t with { Enabled = false };
            retiredNow.Add(t.Address);
            log.Add($"retired {t.Name}");
            changed = true;
        }

        return new Plan(result, retiredNow, changed, log);
    }

    private static string UniqueName(string wanted, List<TargetDto> targets)
    {
        if (!targets.Any(t => t.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            return wanted;
        for (var n = 2; ; n++)
        {
            var candidate = $"{wanted} ({n})";
            if (!targets.Any(t => t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }
}
