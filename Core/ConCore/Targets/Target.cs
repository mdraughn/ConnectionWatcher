namespace ConCore.Targets;

/// <summary>
/// A monitored endpoint: the engine's unit of work.
/// </summary>
public record Target
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    /// <summary>IP literal or DNS hostname.</summary>
    public required string Address { get; init; }
    /// <summary>Optional reachability parent (forms the topology tree).</summary>
    public Guid? ParentId { get; init; }
    public required string ProfileName { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Who runs this hop: an ISP name for trunk targets ("Comcast"),
    /// so attribution can name names. Optional, display-only.</summary>
    public string? Operator { get; init; }
    /// <summary>Optional probe override ("tcp:&lt;port&gt;"); null means ICMP echo.</summary>
    public string? Probe { get; init; } = null;
    /// <summary>Whether this target feeds the verdict (default) or is a LAN device tracked on the side.</summary>
    public TargetScope Scope { get; init; } = TargetScope.Verdict;

    /// <summary>Report this target's own failure and recovery even while the
    /// internet is fine (spec 2026-10-09 §4.7). Never affects the verdict.</summary>
    public bool Critical { get; init; }

    /// <summary>Created and retired by ConnectionWatcher itself from the host's
    /// OS resolver list (spec 2026-10-10 §4.1). The admin may disable it but
    /// not re-address or delete it. Always a Resolver-scope target.</summary>
    public bool SystemManaged { get; init; }
}
