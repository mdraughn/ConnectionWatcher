using ConCore.Targets;

namespace ConEngine.Tests;

public class IspRuleTests
{
    private sealed record N(string Name, string? Op);

    [Theory]
    [InlineData(" Comcast ", "comcast")]
    [InlineData("COMCAST", "comcast")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void OperatorKey_ignores_case_and_padding(string? tag, string? key) => Assert.Equal(key, IspRule.OperatorKey(tag));

    [Fact]
    public void Anchors_sharing_the_trunk_operator_do_not_count()
    {
        var counted = IspRule.CountedAnchors<N>([new N("A", " comcast"), new N("B", null), new N("C", "Google")],
            [new N("Modem", "Comcast")], n => n.Op, out var fallback);
        Assert.Equal(["B", "C"], counted.Select(n => n.Name));
        Assert.False(fallback);
    }

    [Fact]
    public void Every_anchor_the_isps_counts_them_all_as_a_fallback()
    {
        var counted = IspRule.CountedAnchors<N>([new N("A", "Comcast"), new N("B", "comcast")],
            [new N("Modem", "Comcast")], n => n.Op, out var fallback);
        Assert.Equal(2, counted.Count);
        Assert.True(fallback);
    }

    [Fact]
    public void No_trunk_tag_counts_every_anchor_without_fallback()
    {
        var counted = IspRule.CountedAnchors<N>([new N("A", "Comcast")], [new N("Modem", null)], n => n.Op, out var fallback);
        Assert.Single(counted);
        Assert.False(fallback);
    }

    [Fact]
    public void No_anchors_is_no_fallback()
    {
        Assert.Empty(IspRule.CountedAnchors<N>([], [new N("Modem", "Comcast")], n => n.Op, out var fallback));
        Assert.False(fallback);
    }
}
