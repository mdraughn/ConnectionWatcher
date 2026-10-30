using ConCore.Targets;

namespace ConEngine.Tests;

public class TreeWalkTests
{
    private sealed record Node(string Name);

    private static Func<Node, Node?> Parents(Dictionary<string, string> map) =>
        n => map.TryGetValue(n.Name, out var p) ? new Node(p) : null;

    [Fact]
    public void TrunkOf_is_every_anchor_ancestor_minus_the_anchors()
    {
        // Router <- Google (anchor) <- Google DNS (anchor); Router <- Cloudflare (anchor)
        var map = new Dictionary<string, string> { ["Google"] = "Router", ["Google DNS"] = "Google", ["Cloudflare"] = "Router" };
        var anchors = new[] { new Node("Google"), new Node("Google DNS"), new Node("Cloudflare") };
        var trunk = TreeWalk.TrunkOf(anchors, Parents(map));
        Assert.Equal(["Router"], trunk.Select(n => n.Name));
    }

    [Fact]
    public void A_self_loop_terminates_with_the_node_once()
    {
        var chain = TreeWalk.ChainToRoot(new Node("a"), Parents(new() { ["a"] = "a" }));

        Assert.Equal([new Node("a")], chain);
    }

    [Fact]
    public void A_two_cycle_terminates_with_each_node_once()
    {
        var chain = TreeWalk.ChainToRoot(new Node("a"), Parents(new() { ["a"] = "b", ["b"] = "a" }));

        Assert.Equal([new Node("a"), new Node("b")], chain);
    }

    [Fact]
    public void A_chain_deeper_than_the_cap_stops_at_32()
    {
        var map = Enumerable.Range(0, 40).ToDictionary(i => $"n{i}", i => $"n{i + 1}");

        var chain = TreeWalk.ChainToRoot(new Node("n0"), Parents(map));

        Assert.Equal(32, TreeWalk.MaxDepth);
        Assert.Equal(32, chain.Count);
        Assert.Equal("n31", chain[^1].Name);
    }

    [Fact]
    public void ChainToRoot_is_leaf_first_and_root_last()
    {
        var chain = TreeWalk.ChainToRoot(new Node("leaf"), Parents(new() { ["leaf"] = "mid", ["mid"] = "root" }));

        Assert.Equal(["leaf", "mid", "root"], chain.Select(n => n.Name));
    }

    [Fact]
    public void AncestorsOf_excludes_members_dedupes_a_shared_trunk_and_keeps_first_seen_order()
    {
        // x and y both hang under trunk t1 -> t2 (root); z hangs under its own branch u -> t2.
        var map = new Dictionary<string, string>
        {
            ["x"] = "t1", ["y"] = "t1", ["t1"] = "t2", ["z"] = "u", ["u"] = "t2",
        };
        var members = new[] { new Node("x"), new Node("y"), new Node("z") };

        var ancestors = TreeWalk.AncestorsOf(members, Parents(map));

        Assert.Equal(["t1", "t2", "u"], ancestors.Select(n => n.Name));
    }
}
