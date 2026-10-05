using ConCore.Discovery;

namespace ConPersistence.Entities;

/// <summary>
/// A candidate found by a search: an address nobody is monitoring yet. Node-local
/// (never exported or cloned). Unique per (Address, Kind); a Dismissed row stays
/// Dismissed when found again.
/// </summary>
public class DiscoveryRow
{
    public Guid Id { get; set; }
    public string Address { get; set; } = "";
    public string? Hostname { get; set; }
    public DiscoveryKind Kind { get; set; }
    public DiscoveryMethod Method { get; set; }
    /// <summary>Who found it: "server" or "tray:&lt;machine name&gt;".</summary>
    public string Origin { get; set; } = "server";
    /// <summary>Short human-readable proof: RTT, hop number, MAC vendor.</summary>
    public string? Evidence { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DiscoveryStatus Status { get; set; } = DiscoveryStatus.New;
    /// <summary>Set when adopted; may point at a target that has since been deleted.</summary>
    public Guid? AdoptedTargetId { get; set; }
}
