using ConCore.Targets;

namespace ConEngine.Tests;

public class TargetScopeTests
{
    [Theory]
    [InlineData(TargetScope.Verdict, true)]
    [InlineData(TargetScope.Local, false)]
    [InlineData(TargetScope.Resolver, false)]
    public void Only_verdict_targets_are_tree_members(TargetScope scope, bool expected) =>
        Assert.Equal(expected, scope.IsTree());
}
