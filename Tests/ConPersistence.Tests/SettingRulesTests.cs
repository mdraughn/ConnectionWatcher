using ConPersistence.Settings;

namespace ConPersistence.Tests;

public class SettingRulesTests
{
    [Theory]
    // default on: off only for exactly "false"
    [InlineData(SettingKeys.EngineEnabled, null, true)]
    [InlineData(SettingKeys.EngineEnabled, "", true)]
    [InlineData(SettingKeys.EngineEnabled, "false", false)]
    [InlineData(SettingKeys.EngineEnabled, "FALSE", true)]
    [InlineData(SettingKeys.DnsEnabled, "false", false)]
    [InlineData(SettingKeys.WebhookSkipExplained, null, true)]
    [InlineData(SettingKeys.CloneIncludeLocalDevices, "false", false)]
    // default off: on only for exactly "true"
    [InlineData(SettingKeys.MqttEnabled, null, false)]
    [InlineData(SettingKeys.MqttEnabled, "true", true)]
    [InlineData(SettingKeys.MqttEnabled, "TRUE", false)]
    [InlineData(SettingKeys.WebhookEnabled, "true", true)]
    [InlineData(SettingKeys.WebhookVerdictOnly, "yes", false)]
    [InlineData(SettingKeys.TracerouteAsnLookup, "", false)]
    public void Every_flag_reads_as_its_consumers_did(string key, string? raw, bool expected) =>
        Assert.Equal(expected, SettingRules.ReadFlag(key, raw));

    [Fact]
    public void A_key_that_is_not_a_flag_is_refused() =>
        Assert.Throws<ArgumentException>(() => SettingRules.ReadFlag(SettingKeys.MqttPort, "true"));

    [Fact]
    public void Int_errors_name_the_full_key_and_range()
    {
        Assert.Null(SettingRules.IntError(SettingKeys.MinuteRetentionDays, "90"));
        Assert.Equal("Retention.MinuteRetentionDays must be 1–3650.", SettingRules.IntError(SettingKeys.MinuteRetentionDays, "0"));
        Assert.Equal("Retention.RetentionRunHourUtc must be 0–23.", SettingRules.IntError(SettingKeys.RetentionRunHourUtc, "x"));
        Assert.Equal("Verdict.WeatherThreshold must be an integer ≥ 1.", SettingRules.IntError(SettingKeys.WeatherThreshold, null));
    }

    [Fact]
    public void Defaults_match_the_compiled_options()
    {
        var p = new PersistenceOptions();
        Assert.Equal(p.MinuteRetentionDays, SettingRules.Int(SettingKeys.MinuteRetentionDays).Default);
        Assert.Equal(1883, SettingRules.Int(SettingKeys.MqttPort).Default);
        Assert.Equal(5, SettingRules.Int(SettingKeys.HeartbeatIntervalMinutes).Default);
    }
}
