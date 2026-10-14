using ConCore.Targets;
using ConEngine.Configuration;
using Microsoft.Extensions.Options;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-10 §4.1, R1: a Resolver target stands alone — no parent,
/// never a pool member or a pool's parent, never anyone's parent.</summary>
public class ResolverScopeValidationTests
{
    private static ValidateOptionsResult Validate(WatcherOptions options) =>
        new WatcherOptionsValidator().Validate(null, options);

    private static TargetConfig Router => new() { Name = "Router", Address = "203.0.113.1", Profile = "Internal" };
    private static TargetConfig Resolver => new()
    {
        Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Public",
        Probe = "dns", Scope = TargetScope.Resolver,
    };

    [Fact]
    public void A_parentless_Resolver_target_is_valid()
    {
        var result = Validate(new WatcherOptions { Targets = [Router, Resolver] });
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void Address_plus_probe_is_the_uniqueness_key_for_a_Resolver_too()
    {
        // The household resolver usually IS the router: a different probe at the same address is fine...
        var routerPingsOnly = Router;
        routerPingsOnly.Probe = "icmp";
        var different = Validate(new WatcherOptions { Targets = [routerPingsOnly, Resolver] });
        Assert.True(different.Succeeded, string.Join("; ", different.Failures ?? []));

        // ...the same probe at the same address is a duplicate, whatever the scope.
        var routerDns = Router;
        routerDns.Probe = "dns";
        var same = Validate(new WatcherOptions { Targets = [routerDns, Resolver] });
        Assert.True(same.Failed);
        Assert.Contains(same.Failures!, f => f.Contains("Duplicate target address '203.0.113.1' with probe 'dns'"));
    }

    [Fact]
    public void A_Resolver_target_cannot_have_a_parent()
    {
        var resolver = Resolver;
        resolver.Parent = "Router";
        var result = Validate(new WatcherOptions { Targets = [Router, resolver] });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("is Resolver and cannot have a parent"));
    }

    [Fact]
    public void A_Resolver_target_cannot_be_a_pool_member()
    {
        var result = Validate(new WatcherOptions
        {
            Targets = [Router, Resolver],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Your resolver 203.0.113.1"] }],
        });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Resolver targets cannot be pool members"));
    }

    [Fact]
    public void A_Resolver_target_cannot_be_a_parent()
    {
        var child = new TargetConfig { Name = "Child", Address = "203.0.113.2", Parent = "Your resolver 203.0.113.1" };
        var result = Validate(new WatcherOptions { Targets = [Router, Resolver, child] });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Resolver targets cannot have children"));
    }
}
