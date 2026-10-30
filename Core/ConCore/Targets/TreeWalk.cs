namespace ConCore.Targets;

/// <summary>
/// Parent-chain walks over any node shape (config DTOs, stored rows, the live
/// graph): the caller supplies "parent of". Cycle-safe and depth-capped, so
/// a malformed tree can never spin.
/// </summary>
public static class TreeWalk
{
    public const int MaxDepth = 32;

    /// <summary>The node itself followed by its ancestors, leaf first, root last.</summary>
    public static List<T> ChainToRoot<T>(T start, Func<T, T?> parentOf, IEqualityComparer<T>? comparer = null)
        where T : class
    {
        var chain = new List<T>();
        var seen = new HashSet<T>(comparer ?? EqualityComparer<T>.Default);
        var current = start;
        while (current is not null && chain.Count < MaxDepth && seen.Add(current))
        {
            chain.Add(current);
            current = parentOf(current);
        }
        return chain;
    }

    /// <summary>
    /// Every ancestor of every member (members themselves excluded), in
    /// first-seen order walking each member upward, without duplicates. This
    /// is "the trunk" above an anchor pool.
    /// </summary>
    public static List<T> AncestorsOf<T>(IEnumerable<T> members, Func<T, T?> parentOf,
        IEqualityComparer<T>? comparer = null) where T : class
    {
        var result = new List<T>();
        var seen = new HashSet<T>(comparer ?? EqualityComparer<T>.Default);
        foreach (var member in members)
        {
            var parent = parentOf(member);
            if (parent is null)
                continue;
            foreach (var ancestor in ChainToRoot(parent, parentOf, comparer))
            {
                if (seen.Add(ancestor))
                    result.Add(ancestor);
            }
        }
        return result;
    }

    /// <summary>The trunk above these anchors: <see cref="AncestorsOf"/> minus the anchors themselves
    /// (ruling 2026-10-28: an anchor nested under another anchor is never trunk). First-seen order.</summary>
    public static List<T> TrunkOf<T>(IReadOnlyCollection<T> anchors, Func<T, T?> parentOf,
        IEqualityComparer<T>? comparer = null) where T : class
    {
        var anchorSet = new HashSet<T>(anchors, comparer ?? EqualityComparer<T>.Default);
        return AncestorsOf(anchors, parentOf, comparer).Where(a => !anchorSet.Contains(a)).ToList();
    }
}
