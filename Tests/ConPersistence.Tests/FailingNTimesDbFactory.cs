using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>Fails the first N context creations, then delegates.</summary>
public sealed class FailingNTimesDbFactory(IDbContextFactory<ConWatchDbContext> inner, int failures)
    : IDbContextFactory<ConWatchDbContext>
{
    private int _calls;

    public ConWatchDbContext CreateDbContext()
    {
        if (Interlocked.Increment(ref _calls) <= failures)
            throw new InvalidOperationException("simulated SQLITE_BUSY");
        return inner.CreateDbContext();
    }

    public Task<ConWatchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _calls) <= failures)
            throw new InvalidOperationException("simulated SQLITE_BUSY");
        return inner.CreateDbContextAsync(cancellationToken);
    }
}
