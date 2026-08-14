namespace ConCore.Targets;

/// <summary>
/// A virtual aggregate target whose state derives from its members.
/// Pools are never pinged directly.
/// </summary>
public record PoolDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<Guid> MemberTargetIds { get; init; }
    public Guid? ParentId { get; init; }
}
