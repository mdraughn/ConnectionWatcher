using ConInfrastructure.Discovery;

namespace ConEngine.Tests;

public class OuiTableTests
{
    private static readonly OuiTable Small = new(["A4BBCC\tExample Networks", "001122\tOther Vendor"]);

    [Theory]
    [InlineData("a4:bb:cc:00:11:22", "Example Networks")]
    [InlineData("A4-BB-CC-DD-EE-FF", "Example Networks")]
    [InlineData("00:11:22:33:44:55", "Other Vendor")]
    public void Known_prefixes_return_the_vendor_whatever_the_separator_or_case(string mac, string vendor) =>
        Assert.Equal(vendor, Small.Describe(mac));

    [Fact]
    public void An_unknown_prefix_is_null()
        => Assert.Null(Small.Describe("10:20:30:40:50:60"));

    [Theory]
    [InlineData("02:00:00:00:00:01")]
    [InlineData("06:aa:bb:cc:dd:ee")]
    [InlineData("0a:aa:bb:cc:dd:ee")]
    [InlineData("0e:aa:bb:cc:dd:ee")]
    [InlineData("fa:aa:bb:cc:dd:ee")]
    public void A_locally_administered_mac_is_labelled_randomized_not_given_a_vendor(string mac)
    {
        // Even if its prefix collided with a registry entry, a private address says nothing about the maker.
        var table = new OuiTable(["020000\tCollision Corp", "06AABB\tCollision Corp", "0AAABB\tCollision Corp"]);
        Assert.Equal("private (randomized) address", table.Describe(mac));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a mac")]
    [InlineData("aa:bb:cc")]
    public void A_malformed_mac_is_null_not_an_exception(string mac) => Assert.Null(Small.Describe(mac));

    [Fact]
    public void The_embedded_registry_is_present_and_large_and_knows_a_stable_entry()
    {
        var table = OuiTable.Default;
        Assert.True(table.Count > 10_000, $"only {table.Count} entries — was the table generated?");
        Assert.Contains("Cisco", table.Describe("00:00:0c:12:34:56") ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
