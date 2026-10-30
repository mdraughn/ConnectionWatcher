using ConCore.Targets;

namespace ConEngine.Tests;

public class TargetRoleTests
{
    [Fact]
    public void Roles_are_the_documented_defaults()
    {
        Assert.Equal(new TargetRole("Internal", TargetScope.Verdict, Critical: true), TargetRole.Router);
        Assert.Equal(new TargetRole("Path", TargetScope.Verdict), TargetRole.PathHop);
        Assert.Equal(new TargetRole("Public", TargetScope.Verdict), TargetRole.Anchor);
        Assert.Equal(new TargetRole("Local", TargetScope.Local), TargetRole.LocalDevice);
        Assert.Equal(new TargetRole("Resolver", TargetScope.Resolver, Probe: "dns"), TargetRole.Resolver);
    }
}
