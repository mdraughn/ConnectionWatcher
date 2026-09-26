using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ConPersistence.Tests;

/// <summary>
/// A migrated SQLite file (WAL, like production) in its own temp directory,
/// for tests that need real concurrency: a background service polling while
/// the test reads, or two connections interleaving. TestDbFactory's single
/// shared in-memory connection is not safe for either.
/// </summary>
public sealed class FileDbFactory : IDbContextFactory<ConWatchDbContext>, IDisposable
{
    private readonly string? _ownedDir;
    private readonly DbContextOptions<ConWatchDbContext> _options;

    public string DbPath { get; }

    public FileDbFactory()
    {
        _ownedDir = Directory.CreateTempSubdirectory("conpersistence-test").FullName;
        DbPath = Path.Combine(_ownedDir, "test.db");
        _options = Build(DbPath, []);
    }

    private FileDbFactory(string dbPath, IInterceptor[] interceptors)
    {
        DbPath = dbPath;
        _options = Build(dbPath, interceptors);
    }

    /// <summary>A second factory over the same file, with interceptors; it does not own the file.</summary>
    public FileDbFactory With(params IInterceptor[] interceptors) => new(DbPath, interceptors);

    /// <summary>Migrate and switch to WAL, exactly as the app does at startup.</summary>
    public Task PrepareAsync() => DatabaseStartup.PrepareAsync(this, CancellationToken.None);

    public ConWatchDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        if (_ownedDir is null)
            return;
        try
        {
            Directory.Delete(_ownedDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, like ConWatchFactory's temp cleanup.
        }
    }

    private static DbContextOptions<ConWatchDbContext> Build(string path, IInterceptor[] interceptors)
    {
        // Pooling=False: every context really closes its file handle, so the
        // directory can be deleted and two factories really are two connections.
        var builder = new DbContextOptionsBuilder<ConWatchDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False");
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return builder.Options;
    }
}
