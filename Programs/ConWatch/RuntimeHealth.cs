using ConEngine;
using ConPersistence;
using ConPersistence.Config;

namespace ConWatch;

/// <summary>Warnings derivable only from the live engine (ConfigHealth is
/// config-only and cannot see runtime counters).</summary>
public static class RuntimeHealth
{
    public static List<HealthWarning> RefusalWarnings(WatcherEngine? engine)
    {
        var warnings = new List<HealthWarning>();
        foreach (var monitor in engine?.Monitors.Values ?? [])
        {
            if (!monitor.PersistentlyRefused)
                continue;
            warnings.Add(new HealthWarning("tcp-refused",
                $"{monitor.Target.Name} has refused every connection on " +
                $"{monitor.Target.Probe} ({monitor.RefusedReplies} probes) — nothing is " +
                "listening there. The device is up, but use the Test button to find a port " +
                "that accepts."));
        }
        return warnings;
    }
}
