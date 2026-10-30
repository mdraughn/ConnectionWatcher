using ConCore.Targets;

namespace ConEngine.Tests;

public class TargetNamesTests
{
    [Fact] public void A_free_name_is_kept_trimmed() =>
        Assert.Equal("Router", TargetNames.Unique("  Router ", ["Modem"]));
    [Fact] public void Taken_names_compare_case_insensitively() =>
        Assert.Equal("Router 2", TargetNames.Unique("Router", ["router"]));
    [Fact] public void The_first_free_number_wins() =>
        Assert.Equal("Router 4", TargetNames.Unique("Router", ["Router", "Router 2", "ROUTER 3"]));
    [Fact] public void The_result_never_exceeds_the_limit_even_with_a_suffix()
    {
        var longName = new string('a', 70);
        Assert.Equal(new string('a', 64), TargetNames.Unique(longName, []));
        var suffixed = TargetNames.Unique(longName, [new string('a', 64)]);
        Assert.Equal(new string('a', 62) + " 2", suffixed);
    }
}
