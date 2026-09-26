using ConEngine.Configuration;
using ConPersistence.Settings;

namespace ConPersistence.Tests;

public class SettingsDefaultsTests
{
    [Fact]
    public void For_lists_every_seeded_key_from_the_options()
    {
        var defaults = SettingsDefaults.For(
            new VerdictOptions { AnchorPool = "Internet", WeatherThreshold = 3 },
            new PersistenceOptions
            {
                MinuteRetentionDays = 30, HourlyRetentionMonths = 6,
                TransitionRetentionMonths = 12, RetentionRunHourUtc = 4,
            });

        Assert.Equal(new Dictionary<string, string>
        {
            [SettingKeys.WeatherThreshold] = "3",
            [SettingKeys.MinuteRetentionDays] = "30",
            [SettingKeys.HourlyRetentionMonths] = "6",
            [SettingKeys.TransitionRetentionMonths] = "12",
            [SettingKeys.RetentionRunHourUtc] = "4",
            [SettingKeys.WarmRestartMaxGapMinutes] = "10",
            [SettingKeys.AnchorPool] = "Internet",
        }, defaults);
    }

    [Fact]
    public void For_omits_the_anchor_pool_when_none_is_configured() =>
        Assert.False(SettingsDefaults.For(new VerdictOptions(), new PersistenceOptions())
            .ContainsKey(SettingKeys.AnchorPool));
}
