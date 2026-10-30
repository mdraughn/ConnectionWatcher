using ConCore.Targets;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Queries;

/// <summary>The current target scopes, loaded once per request, for tagging history rows with a scope.</summary>
public sealed class ScopeTags
{
    private readonly ISet<Guid> _localIds;
    private readonly ISet<Guid> _resolverIds;
    private readonly ISet<Guid> _knownIds;

    public ScopeTags(ISet<Guid> localIds, ISet<Guid> resolverIds, ISet<Guid> knownIds)
    {
        _localIds = localIds;
        _resolverIds = resolverIds;
        _knownIds = knownIds;
    }

    /// <summary>One query over the configured targets.</summary>
    public static async Task<ScopeTags> LoadAsync(ConWatchDbContext db)
    {
        var targets = await db.Targets.AsNoTracking().Select(t => new { t.Id, t.Scope }).ToListAsync();
        return new ScopeTags(
            targets.Where(t => t.Scope == TargetScope.Local).Select(t => t.Id).ToHashSet(),
            targets.Where(t => t.Scope == TargetScope.Resolver).Select(t => t.Id).ToHashSet(),
            targets.Select(t => t.Id).ToHashSet());
    }

    /// <summary>The scope tag history rows carry: a verdict/pool subject or a live non-Local
    /// target is "Verdict"; a live Local or Resolver target its own; a Target subject that is no
    /// longer configured is "Unknown" — it stays under scope=all and is off both filters.</summary>
    public string Of(Guid id, string subjectKind) =>
        _localIds.Contains(id) ? "Local"
        : _resolverIds.Contains(id) ? "Resolver"
        : subjectKind != "Target" || _knownIds.Contains(id) ? "Verdict"
        : "Unknown";
}
