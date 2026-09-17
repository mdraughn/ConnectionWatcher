using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>Throws on the first context request (a simulated SQLITE_BUSY), then delegates.</summary>
public sealed class FailingOnceDbFactory(IDbContextFactory<ConWatchDbContext> inner) : IDbContextFactory<ConWatchDbContext>
{
    private int _calls;

    public ConWatchDbContext CreateDbContext()
    {
        if (Interlocked.Increment(ref _calls) == 1)
            throw new InvalidOperationException("simulated SQLITE_BUSY");
        return inner.CreateDbContext();
    }

    public Task<ConWatchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _calls) == 1)
            throw new InvalidOperationException("simulated SQLITE_BUSY");
        return inner.CreateDbContextAsync(cancellationToken);
    }
}
