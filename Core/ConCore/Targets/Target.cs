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
}
