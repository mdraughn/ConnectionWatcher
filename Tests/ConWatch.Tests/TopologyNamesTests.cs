using ConPersistence.Config;

namespace ConWatch.Tests;

public class TopologyNamesTests
{
    /// <summary>Two anchors under two different WAN hops: the case the three old rules disagreed on.</summary>
    private static ConfigDocument TwoWans() => new()
    {
        Targets =
        [
            new() { Name = "Router", Address = "192.168.1.1" },
            new() { Name = "WanA", Address = "192.168.100.1", Parent = "Router" },
            new() { Name = "WanB", Address = "192.168.200.1", Parent = "Router" },
            new() { Name = "AnchorA", Address = "8.8.8.8", Parent = "WanA" },
            new() { Name = "AnchorB", Address = "1.1.1.1", Parent = "WanB" },
        ],
        Pools = [new() { Name = "Internet", Members = ["AnchorA", "AnchorB"], Parent = "WanA" }],
    };

    [Fact]
    public void Trunk_is_the_union_of_every_anchor_ancestor_chain()
    {
        var names = TopologyNames.Compute(TwoWans(), "internet", weatherThreshold: 2); // pool lookup ignores case

        Assert.True(names.HasAnchorPool);
        Assert.Equal(new[] { "AnchorA", "AnchorB" }, names.AnchorNames);
        Assert.Equal(new[] { "WanA", "Router", "WanB" }, names.TrunkNames);
        Assert.Equal(2, names.TrunkChains.Count);
        Assert.Equal(new[] { "Router", "WanA" }, names.TrunkChains[0]);
        Assert.Equal(new[] { "Router", "WanB" }, names.TrunkChains[1]);
        Assert.Equal(2, names.WeatherThreshold);
    }

    [Fact]
    public void Anchors_sharing_a_parent_give_one_chain()
    {
        var doc = new ConfigDocument
        {
            Targets =
            [
                new() { Name = "Router", Address = "192.168.1.1" },
                new() { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new() { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
            ],
            Pools = [new() { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        };

        var names = TopologyNames.Compute(doc, "Internet", weatherThreshold: 2);

        Assert.Equal(new[] { "Router" }, names.TrunkNames);
        Assert.Equal(new[] { "Router" }, Assert.Single(names.TrunkChains));
    }

    [Fact]
    public void An_unknown_anchor_pool_gives_no_roles()
    {
        var names = TopologyNames.Compute(TwoWans(), "Nope", weatherThreshold: 2);

        Assert.False(names.HasAnchorPool);
        Assert.Empty(names.TrunkNames);
        Assert.Empty(names.AnchorNames);
        Assert.Empty(names.TrunkChains);
    }
}
