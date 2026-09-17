using Microsoft.EntityFrameworkCore;

namespace ConPersistence;

/// <summary>
/// One-time database preparation, run before any hosted service starts:
/// apply migrations, then switch a file-backed database to WAL so readers
/// (history, exports, the report) never block behind the recorders' or the
/// retention pass's write transactions. Idempotent; WAL persists in the file.
/// </summary>
public static class DatabaseStartup
{
    public static async Task PrepareAsync(IDbContextFactory<ConWatchDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        var source = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrEmpty(source) || source.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            return; // in-memory databases have no journal to speak of

        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
