using ConPersistence.Config;

namespace ConPersistence.Tests;

public class HealthWarningPageTests
{
    [Theory]
    [InlineData("dns-not-watched", "/Config/Dns", "DNS")]
    [InlineData("no-upstream-resolver", "/Config/Dns", "DNS")]
    [InlineData("dns-over-https", "/Config/Dns", "DNS")]
    [InlineData("no-anchor-pool", "/Config/Pools", "Pools")]
    [InlineData("anchor-pool-missing", "/Config/Pools", "Pools")]
    [InlineData("anchors-below-threshold", "/Config/Pools", "Pools")]
    [InlineData("anchors-below-recommended", "/Config/Pools", "Pools")]
    [InlineData("pool-parent-drift", "/Config/Pools", "Pools")]
    [InlineData("anchors-all-isp", "/Config/Targets", "Targets")]
    [InlineData("tcp-refused", "/Config/Targets", "Targets")]
    [InlineData("clone-source-active", "/Settings", "Settings")]
    [InlineData("something-new", "/Config/Targets", "Targets")]
    public void Each_warning_names_the_page_where_it_is_fixed(string code, string page, string pageName)
    {
        var warning = new HealthWarning(code, "m");
        Assert.Equal(page, warning.Page);
        Assert.Equal(pageName, warning.PageName);
    }

    [Fact]
    public void IsConfigTab_is_true_only_for_config_pages()
    {
        Assert.True(new HealthWarning("dns-not-watched", "m").IsConfigTab);
        Assert.False(new HealthWarning("clone-source-active", "m").IsConfigTab);
    }
}
