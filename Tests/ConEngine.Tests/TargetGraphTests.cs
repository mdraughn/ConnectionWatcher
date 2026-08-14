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
        var names = g.GetDescendants(router.Id).Select(t => t.Name).Order().ToArray();
        Assert.Equal(new[] { "Cloudflare", "Google", "Modem", "TV" }, names);
        Assert.DoesNotContain("Router", names);
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
