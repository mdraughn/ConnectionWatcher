using ConCore.Dns;
using ConEngine.Configuration;
using ConPersistence.Settings;

namespace ConPersistence.Tests;

public class DnsSettingsTests
{
    [Fact]
    public void Defaults_seed_the_watch_on_with_the_four_names()
    {
        var defaults = SettingsDefaults.For(new VerdictOptions(), new PersistenceOptions());
        Assert.Equal("true", defaults[SettingKeys.DnsEnabled]);
        Assert.Equal(DnsLookupName.Seeded, DnsLookupName.FromJson(defaults[SettingKeys.DnsLookupNames]));
        Assert.False(defaults.ContainsKey(SettingKeys.DnsRetiredResolvers)); // the service writes it, nothing seeds it
    }

    [Fact]
    public void Retired_resolvers_are_per_node() =>
        Assert.Contains(SettingKeys.DnsRetiredResolvers, SettingKeys.PerNodeKeys);
}
