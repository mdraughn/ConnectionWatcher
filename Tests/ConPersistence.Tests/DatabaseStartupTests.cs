using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

public class DatabaseStartupTests
{
    /// <summary>File-backed factory: TestDbFactory is :memory:, where WAL cannot be observed.</summary>
    private sealed class FileDbFactory(DbContextOptions<ConWatchDbContext> options) : IDbContextFactory<ConWatchDbContext>
    {
        public ConWatchDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Prepare_migrates_and_switches_a_file_database_to_wal()
    {
        var dir = Directory.CreateTempSubdirectory("conwatch-wal");
        try
        {
            var path = Path.Combine(dir.FullName, "t.db");
            var options = new DbContextOptionsBuilder<ConWatchDbContext>().UseSqlite($"Data Source={path}").Options;
            IDbContextFactory<ConWatchDbContext> factory = new FileDbFactory(options); // CreateDbContextAsync is a default interface member

            await DatabaseStartup.PrepareAsync(factory, CancellationToken.None);

            await using var db = await factory.CreateDbContextAsync();
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());

            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("wal", mode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort on Windows */ }
        }
    }
}
