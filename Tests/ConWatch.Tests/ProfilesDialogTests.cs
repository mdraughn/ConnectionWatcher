using System.Globalization;
using ConWatch.Pages.Config;

namespace ConWatch.Tests;

public class ProfilesDialogTests
{
    [Fact]
    public void Dialog_defaults_are_invariant_whatever_the_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var d = ProfilesModel.DialogDefaults;
            Assert.Equal("10", d["normalIntervalSeconds"]);
            Assert.Equal("0.95", d["respondingThreshold"]);
            Assert.Equal("100", d["historySize"]);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }
}
