using System.Data.Common;
using ConPersistence.Config;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ConPersistence.Tests;

public class ConfigSnapshotReadTests
{
    /// <summary>Runs one write, once, the moment the first query against
    /// Profiles has opened its reader: between the profile read and the
    /// target read of a whole-config load.</summary>
    private sealed class WriteBetweenReads(Func<Task> write) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"Profiles\"") && Interlocked.Exchange(ref _fired, 1) == 0)
                await write();
            return result;
        }
    }

    // A document without ids: every import mints new profile and target ids,
    // so a load that mixes old profiles with new targets cannot map
    // ProfileId -> name.
    private static ConfigDocument Home(string routerAddress) => new()
    {
        Profiles = [new ProfileDto { Name = "Internal" }],
        Targets = [new TargetDto { Name = "Router", Address = routerAddress, Profile = "Internal" }],
    };

    private static async Task<ConfigService> SeededWriterAsync(FileDbFactory files)
    {
        await files.PrepareAsync();
        var writer = new ConfigService(files, new ConfigStore(files));
        Assert.True((await writer.ImportAsync(Home("192.168.1.1"))).Success);
        return writer;
    }

    [Fact]
    public async Task Load_reads_one_snapshot_while_an_import_commits()
    {
        using var files = new FileDbFactory();
        var writer = await SeededWriterAsync(files);
        var reader = new ConfigStore(files.With(new WriteBetweenReads(async () =>
            Assert.True((await writer.ImportAsync(Home("192.168.1.2"))).Success))));

        var options = await reader.LoadWatcherOptionsAsync();

        Assert.Equal("192.168.1.1", Assert.Single(options.Targets).Address); // the state before the import
        Assert.Equal("192.168.1.2",
            Assert.Single((await new ConfigStore(files).LoadWatcherOptionsAsync()).Targets).Address);
    }

    [Fact]
    public async Task Export_reads_one_snapshot_while_an_import_commits()
    {
        using var files = new FileDbFactory();
        var writer = await SeededWriterAsync(files);
        var reader = new ConfigStore(files.With(new WriteBetweenReads(async () =>
            Assert.True((await writer.ImportAsync(Home("192.168.1.2"))).Success))));

        var doc = await reader.ExportAsync();

        Assert.Equal("192.168.1.1", Assert.Single(doc.Targets).Address);
        Assert.Equal(doc.Targets[0].Profile, Assert.Single(doc.Profiles).Name);
    }
}
