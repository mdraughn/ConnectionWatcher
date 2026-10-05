using ConCore.Discovery;
using ConInfrastructure.Discovery;
using ConPersistence.Discoveries;
using ConWatch.Discoveries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConWatch.Tests;

public class DiscoverySearchServiceTests : IDisposable
{
    private sealed class HangingScanner : IDiscoveryScanner
    {
        public Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct) => Hang(ct);
        public Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct) => Hang(ct);
        private static async Task<IReadOnlyList<DiscoveryCandidate>> Hang(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
    }

    private sealed class ThrowingScanner : IDiscoveryScanner
    {
        public Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct) =>
            throw new ArgumentException($"No IPv4 address for '{host}'.");
    }

    /// <summary>In-memory SQLite with the real schema (ConWatch.Tests cannot see ConPersistence.Tests' factory).</summary>
    private sealed class MemoryDb : Microsoft.EntityFrameworkCore.IDbContextFactory<ConPersistence.ConWatchDbContext>, IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("Data Source=:memory:");
        private readonly Microsoft.EntityFrameworkCore.DbContextOptions<ConPersistence.ConWatchDbContext> _options;

        public MemoryDb()
        {
            _connection.Open();
            _options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ConPersistence.ConWatchDbContext>()
                .UseSqlite(_connection).Options;
            using var db = new ConPersistence.ConWatchDbContext(_options);
            db.Database.EnsureCreated();
        }

        public ConPersistence.ConWatchDbContext CreateDbContext() => new(_options);
        public void Dispose() => _connection.Dispose();
    }

    private readonly MemoryDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_search_that_exceeds_the_cap_reports_a_timeout_and_frees_the_gate()
    {
        var service = new DiscoverySearchService(new HangingScanner(), new DiscoveryStore(_db),
            NullLogger<DiscoverySearchService>.Instance, cap: TimeSpan.FromMilliseconds(100));

        var first = await service.SearchRouterAsync(CancellationToken.None);
        var second = await service.SearchRouterAsync(CancellationToken.None);   // the gate was released

        Assert.True(first.TimedOut);
        Assert.False(first.Busy);
        Assert.True(second.TimedOut);
        Assert.False(second.Busy);
    }

    [Fact]
    public async Task An_unresolvable_edge_host_is_an_error_not_a_crash_and_frees_the_gate()
    {
        var service = new DiscoverySearchService(new ThrowingScanner(), new DiscoveryStore(_db),
            NullLogger<DiscoverySearchService>.Instance);

        var outcome = await service.SearchEdgeAsync("nope.invalid", CancellationToken.None);

        Assert.Contains(outcome.Errors, e => e.Contains("nope.invalid"));
        Assert.False(outcome.Busy);
    }
}
