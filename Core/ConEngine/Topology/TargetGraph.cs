using ConCore.Targets;
using ConEngine.Configuration;

namespace ConEngine.Topology;

/// <summary>
/// Immutable view of the configured targets, pools, profiles, and the
/// reachability tree. Built from validated WatcherOptions.
/// </summary>
public class TargetGraph
{
    private readonly Dictionary<Guid, Target> _targets;
    private readonly Dictionary<Guid, PoolDefinition> _pools;
    private readonly Dictionary<string, Guid> _targetIdsByName;
    private readonly Dictionary<string, Guid> _poolIdsByName;
    private readonly Dictionary<string, PingProfile> _profiles;
    private readonly ILookup<Guid, Target> _childrenByParent;

    public IReadOnlyDictionary<Guid, Target> Targets => _targets;
    public IReadOnlyDictionary<Guid, PoolDefinition> Pools => _pools;

    private TargetGraph(Dictionary<Guid, Target> targets, Dictionary<Guid, PoolDefinition> pools,
        Dictionary<string, PingProfile> profiles)
    {
        _targets = targets;
        _pools = pools;
        _profiles = profiles;
        _targetIdsByName = targets.Values.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
        _poolIdsByName = pools.Values.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);
        _childrenByParent = targets.Values
            .Where(t => t.ParentId is not null)
            .ToLookup(t => t.ParentId!.Value);
    }

    /// <summary>Builds the graph. Options must already have passed WatcherOptionsValidator.</summary>
    public static TargetGraph FromOptions(WatcherOptions options)
    {
        var profiles = PingProfile.BuiltIns.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var (profileName, config) in options.Profiles)
            profiles[profileName] = config.ToProfile(profileName);

        var idsByName = options.Targets.ToDictionary(t => t.Name, t => t.Id ?? Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);

        var targets = options.Targets.ToDictionary(
            t => idsByName[t.Name],
            t => new Target
            {
                Id = idsByName[t.Name],
                Name = t.Name,
                Address = t.Address,
                ParentId = t.Parent is { Length: > 0 } p ? idsByName[p] : null,
                ProfileName = t.Profile,
                Enabled = t.Enabled,
                Operator = t.Operator,
                Probe = t.Probe,
                Scope = t.Scope,
                Critical = t.Critical,
                SystemManaged = t.SystemManaged,
            });

        var poolDefs = options.Pools
            .Select(p =>
            {
                var id = p.Id ?? Guid.NewGuid();
                return new PoolDefinition
                {
                    Id = id,
                    Name = p.Name,
                    MemberTargetIds = p.Members.Select(m => idsByName[m]).ToList(),
                    ParentId = p.Parent is { Length: > 0 } parent ? idsByName[parent] : null,
                };
            })
            .ToDictionary(p => p.Id);

        return new TargetGraph(targets, poolDefs, profiles);
    }

    internal Target GetByName(string name) => _targets[_targetIdsByName[name]];
    internal PoolDefinition GetPoolByName(string name) => _pools[_poolIdsByName[name]];
    public PingProfile ProfileFor(Target target) => _profiles[target.ProfileName];

    /// <summary>Direct children of a target.</summary>
    internal IReadOnlyList<Target> GetChildren(Guid parentId) => _childrenByParent[parentId].ToList();

    /// <summary>The target's ancestors, root first, itself excluded; only those <paramref name="include"/>
    /// accepts (all when null), walking through the rest. Cycle-safe; capped at TreeWalk.MaxDepth.</summary>
    public IReadOnlyList<Guid> AncestorIds(Guid targetId, Func<Guid, bool>? include = null)
    {
        var chain = TreeWalk.ChainToRoot(_targets[targetId], ParentOf);
        var ids = new List<Guid>(chain.Count);
        for (var i = chain.Count - 1; i >= 1; i--)
            if (include is null || include(chain[i].Id))
                ids.Add(chain[i].Id);
        return ids;
    }

    /// <summary>Transitive descendants, breadth-first (nearest first), itself excluded; filtered like
    /// AncestorIds. Cycle-safe.</summary>
    public IReadOnlyList<Guid> DescendantIds(Guid targetId, Func<Guid, bool>? include = null)
    {
        var result = new List<Guid>();
        var visited = new HashSet<Guid> { targetId };
        var queue = new Queue<Guid>();
        queue.Enqueue(targetId);
        while (queue.Count > 0)
        {
            foreach (var child in _childrenByParent[queue.Dequeue()])
            {
                if (!visited.Add(child.Id))
                    continue;
                if (include is null || include(child.Id))
                    result.Add(child.Id);
                queue.Enqueue(child.Id);
            }
        }
        return result;
    }

    /// <summary>Every ancestor of any of <paramref name="anchorIds"/>, minus those anchors, first-seen
    /// order walking each anchor upward; filtered like AncestorIds. Callers pass the anchors they monitor.</summary>
    public IReadOnlyList<Guid> TrunkIds(IEnumerable<Guid> anchorIds, Func<Guid, bool>? include = null) =>
        TreeWalk.TrunkOf(anchorIds.Select(id => _targets[id]).ToList(), ParentOf)
            .Select(t => t.Id).Where(id => include is null || include(id)).ToList();

    private Target? ParentOf(Target t) =>
        t.ParentId is { } p && _targets.TryGetValue(p, out var parent) ? parent : null;
}
