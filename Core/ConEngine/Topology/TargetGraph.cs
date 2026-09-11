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
        var profiles = new Dictionary<string, PingProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Internal"] = PingProfile.Internal,
            ["External"] = PingProfile.External,
        };
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

    public Target GetByName(string name) => _targets[_targetIdsByName[name]];
    public PoolDefinition GetPoolByName(string name) => _pools[_poolIdsByName[name]];
    public PingProfile ProfileFor(Target target) => _profiles[target.ProfileName];

    /// <summary>Direct children of a target.</summary>
    public IReadOnlyList<Target> GetChildren(Guid parentId) => _childrenByParent[parentId].ToList();

    /// <summary>All transitive descendants of a target (excluding itself).</summary>
    public IReadOnlyList<Target> GetDescendants(Guid ancestorId)
    {
        var result = new List<Target>();
        var queue = new Queue<Guid>();
        queue.Enqueue(ancestorId);
        while (queue.Count > 0)
        {
            foreach (var child in _childrenByParent[queue.Dequeue()])
            {
                result.Add(child);
                queue.Enqueue(child.Id);
            }
        }
        return result;
    }
}
