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
    public void A_Resolver_may_share_the_routers_address_with_a_different_probe()
    {
        // The household resolver usually IS the router; "address + probe" is the uniqueness key.
        var result = Validate(new WatcherOptions { Targets = [Router, Resolver] });
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
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
