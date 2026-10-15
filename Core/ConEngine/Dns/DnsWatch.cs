using ConEngine.Tracking;

namespace ConEngine.Dns;

/// <summary>The name-resolution watch as the engine sees it (spec 2026-10-10
/// §4.4): whether it is on (enabled and there are lookup names), the enabled
/// system-managed resolver monitors, and the enabled upstream (admin-added)
/// resolver monitors.</summary>
public sealed record DnsWatch(bool Enabled, IReadOnlyList<TargetMonitor> System, IReadOnlyList<TargetMonitor> Upstream)
{
    public static DnsWatch Off { get; } = new(false, [], []);
}
