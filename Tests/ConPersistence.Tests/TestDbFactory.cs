using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>
/// In-memory SQLite factory for unit tests: one shared open connection so the
/// schema survives across contexts.
/// </summary>
public sealed class TestDbFactory : IDbContextFactory<ConWatchDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ConWatchDbContext> _options;

    public TestDbFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ConWatchDbContext>()
            .UseSqlite(_connection).Options;
        using var db = new ConWatchDbContext(_options);
        db.Database.EnsureCreated();
    }

    public ConWatchDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
