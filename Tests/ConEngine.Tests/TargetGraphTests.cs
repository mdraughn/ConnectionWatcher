using ConEngine.Configuration;
using ConEngine.Topology;

namespace ConEngine.Tests;

public class TargetGraphTests
{
    private static TargetGraph Home() => TargetGraph.FromOptions(new WatcherOptions
    {
        Profiles = { ["Slow"] = new PingProfileConfig { NormalIntervalSeconds = 30 } },
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
            new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
            new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
            new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem", Profile = "Slow" },
            new TargetConfig { Name = "TV", Address = "192.168.1.50", Parent = "Router", Profile = "Internal" },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"], Parent = "Modem" }],
    });

    [Fact]
    public void Builds_all_targets_and_pools()
    {
        var g = Home();
        Assert.Equal(5, g.Targets.Count);
        Assert.Single(g.Pools);
    }

    [Fact]
    public void Parent_links_resolve_to_ids()
    {
        var g = Home();
        var router = g.GetByName("Router");
        var modem = g.GetByName("Modem");
        Assert.Null(router.ParentId);
        Assert.Equal(router.Id, modem.ParentId);
    }

    [Fact]
    public void Descendants_are_transitive_and_exclude_self()
    {
        var g = Home();
        var router = g.GetByName("Router");
        var names = g.DescendantIds(router.Id).Select(id => g.Targets[id].Name).Order().ToArray();
        Assert.Equal(new[] { "Cloudflare", "Google", "Modem", "TV" }, names);
        Assert.DoesNotContain("Router", names);
    }

    [Fact]
    public void AncestorIds_are_root_first_and_filtered_walking_through_the_rest()
    {
        var g = Home();
        var google = g.GetByName("Google").Id;
        Assert.Equal([g.GetByName("Router").Id, g.GetByName("Modem").Id], g.AncestorIds(google));
        var router = g.GetByName("Router").Id;
        Assert.Equal([router], g.AncestorIds(google, id => id == router)); // Modem skipped, walked through
    }

    [Fact]
    public void DescendantIds_are_breadth_first()
    {
        var g = Home();
        var names = g.DescendantIds(g.GetByName("Router").Id).Select(id => g.Targets[id].Name).ToList();
        Assert.Equal(["Modem", "TV"], names.Take(2).Order());   // depth 1 before depth 2
        Assert.Equal(["Cloudflare", "Google"], names.Skip(2).Order());
    }

    [Fact]
    public void Trunk_comes_only_from_the_anchors_given()
    {
        var g = Home();
        var trunk = g.TrunkIds([g.GetByName("Google").Id]).Select(id => g.Targets[id].Name);
        Assert.Equal(["Modem", "Router"], trunk);
        Assert.Empty(g.TrunkIds([g.GetByName("Router").Id])); // a root anchor has no trunk
    }

    [Fact]
    public void Walks_end_on_a_parent_cycle()
    {
        // FromOptions does not validate, so a cycle can reach the graph.
        var g = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "A", Address = "10.0.0.1", Parent = "B" },
                new TargetConfig { Name = "B", Address = "10.0.0.2", Parent = "A" },
            ],
        });
        var a = g.GetByName("A").Id;
        Assert.Equal([g.GetByName("B").Id], g.AncestorIds(a));
        Assert.Equal([g.GetByName("B").Id], g.DescendantIds(a));
    }

    [Fact]
    public void Children_are_direct_only()
    {
        var g = Home();
        var names = g.GetChildren(g.GetByName("Router").Id).Select(t => t.Name).ToHashSet();
        Assert.Equal(2, names.Count);
        Assert.Contains("Modem", names);
        Assert.Contains("TV", names);
    }

    [Fact]
    public void ProfileFor_resolves_builtins_and_custom()
    {
        var g = Home();
        Assert.Equal(TimeSpan.FromSeconds(1), g.ProfileFor(g.GetByName("Router")).NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), g.ProfileFor(g.GetByName("Google")).NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), g.ProfileFor(g.GetByName("Cloudflare")).NormalInterval);
    }

    [Fact]
    public void Pool_members_resolve_to_target_ids()
    {
        var g = Home();
        var pool = g.GetPoolByName("Internet");
        var memberNames = pool.MemberTargetIds.Select(id => g.Targets[id].Name).ToHashSet();
        Assert.Contains("Google", memberNames);
        Assert.Contains("Cloudflare", memberNames);
        Assert.Equal(g.GetByName("Modem").Id, pool.ParentId);
    }
}
