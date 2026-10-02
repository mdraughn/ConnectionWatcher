using ConCore.Targets;
using ConEngine.Configuration;
using Microsoft.Extensions.Options;

namespace ConEngine.Tests;

public class LocalScopeValidationTests
{
    private static ValidateOptionsResult Validate(WatcherOptions options) =>
        new WatcherOptionsValidator().Validate(null, options);

    private static TargetConfig Router => new() { Name = "Router", Address = "203.0.113.1", Profile = "Internal" };
    private static TargetConfig Nas => new() { Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local };

    [Fact]
    public void A_Local_root_target_is_valid()
    {
        var result = Validate(new WatcherOptions { Targets = [Router, Nas] });
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void A_Local_target_cannot_have_a_parent()
    {
        var nas = Nas;
        nas.Parent = "Router";
        var result = Validate(new WatcherOptions { Targets = [Router, nas] });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Target 'NAS' is Local and cannot have a parent."));
    }

    [Fact]
    public void A_target_cannot_have_a_Local_parent()
    {
        var tv = new TargetConfig { Name = "TV", Address = "203.0.113.10", Profile = "Internal", Parent = "NAS" };
        var result = Validate(new WatcherOptions { Targets = [Nas, tv] });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f =>
            f.Contains("Target 'TV' has Local target 'NAS' as parent; Local targets cannot have children."));
    }

    [Fact]
    public void A_Local_target_cannot_be_a_pool_member()
    {
        var result = Validate(new WatcherOptions
        {
            Targets = [Router, Nas],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Router", "NAS"] }],
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f =>
            f.Contains("Pool 'Internet' member 'NAS' is a Local target; Local targets cannot be pool members."));
    }

    [Fact]
    public void A_pool_cannot_hang_under_a_Local_target()
    {
        var result = Validate(new WatcherOptions
        {
            Targets = [Router, Nas],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Router"], Parent = "NAS" }],
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f =>
            f.Contains("Pool 'Internet' has Local target 'NAS' as parent; Local targets cannot have children."));
    }
}
