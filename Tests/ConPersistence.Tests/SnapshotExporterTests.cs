using System.Data.Common;
using ConPersistence.Config;
using ConPersistence.Settings;
using ConPersistence.Snapshots;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ConPersistence.Tests;

public class SnapshotExporterTests
{
    /// <summary>Runs one write, once, the moment the export's first query has opened its reader.</summary>
    private sealed class WriteAfterFirstRead(Func<Task> write) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                await write();
            return result;
        }
    }

    private static ConfigDocument Home(string routerAddress) => new()
    {
        Profiles = [new ProfileDto { Name = "Internal" }],
        Targets = [new TargetDto { Name = "Router", Address = routerAddress, Profile = "Internal" }],
    };

    [Fact]
    public async Task Export_reads_settings_config_and_measurements_from_one_moment()
    {
        // An import (or a recorder write) committing mid-export must not leave
        // settings from before it next to config from after it.
        using var files = new FileDbFactory();
        await files.PrepareAsync();
        var writer = new ConfigService(files, new ConfigStore(files));
        Assert.True((await writer.ImportAsync(Home("192.168.1.1"))).Success);
        await new SettingsStore(files).SetManyAsync(new Dictionary<string, string?> { ["Verdict.WeatherThreshold"] = "2" });

        var reading = files.With(new WriteAfterFirstRead(async () =>
        {
            Assert.True((await writer.ImportAsync(Home("192.168.1.2"))).Success);
            await new SettingsStore(files).SetManyAsync(new Dictionary<string, string?> { ["Verdict.WeatherThreshold"] = "3" });
        }));

        var snapshot = await SnapshotExporter.ExportAsync(reading, new ConfigStore(reading), TimeProvider.System, includeSecrets: false);

        Assert.Equal("2", snapshot.Settings["Verdict.WeatherThreshold"]);
        Assert.Equal("192.168.1.1", Assert.Single(snapshot.Config.Targets).Address); // same moment as the settings
    }

    [Fact]
    public async Task Secrets_are_left_out_and_named_unless_asked_for()
    {
        using var files = new FileDbFactory();
        await files.PrepareAsync();
        await new SettingsStore(files).SetManyAsync(new Dictionary<string, string?> { [SettingKeys.MqttPassword] = "hunter2" });

        var open = await SnapshotExporter.ExportAsync(files, new ConfigStore(files), TimeProvider.System, includeSecrets: false);
        var full = await SnapshotExporter.ExportAsync(files, new ConfigStore(files), TimeProvider.System, includeSecrets: true);

        Assert.False(open.Settings.ContainsKey(SettingKeys.MqttPassword));
        Assert.Contains(SettingKeys.MqttPassword, open.RedactedSettings);
        Assert.Equal("hunter2", full.Settings[SettingKeys.MqttPassword]);
    }
}
