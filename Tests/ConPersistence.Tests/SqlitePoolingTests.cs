using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace ConPersistence.Tests;

/// <summary>
/// Pins the Microsoft.Data.Sqlite pooling fix the app depends on (dotnet/efcore#39008, fixed in 10.0.13).
/// Up to 10.0.12, a pooled connection was marked active before its owner was recorded, so a concurrent
/// open that found the pool empty could reclaim it as "leaked" and hand the same native connection to a
/// second live SqliteConnection. In the app that surfaced as rare 500s under load ("unable to
/// delete/modify user-function due to active statements" when one owner closed while the other was
/// mid-statement). This is the upstream regression test, over the app's own connection-string shape.
/// </summary>
public class SqlitePoolingTests
{
    [Fact]
    public async Task Concurrent_opens_never_share_a_native_connection()
    {
        const int workerCount = 16;
        const int iterations = 1000;
        var dir = Directory.CreateTempSubdirectory("conpersistence-pool");
        // Exactly the app's shape (ConWatchServices): pooling on by default.
        var connectionString = $"Data Source={Path.Combine(dir.FullName, "pool.db")}";
        var connections = new SqliteConnection?[workerCount];
        var errors = new ConcurrentQueue<Exception>();
        var duplicate = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var barrier = new Barrier(workerCount + 1);
        using var pool = new SqliteConnection(connectionString);

        // Waves: every worker opens at once, the handles are compared while all are open, then all close.
        var workers = Enumerable.Range(0, workerCount).Select(index => Task.Factory.StartNew(() =>
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                barrier.SignalAndWait(timeout.Token);
                try
                {
                    connections[index] = new SqliteConnection(connectionString);
                    connections[index]!.Open();
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }

                try
                {
                    barrier.SignalAndWait(timeout.Token);
                    // Every owner stays alive and open until its handle has been inspected.
                    barrier.SignalAndWait(timeout.Token);
                }
                finally
                {
                    try { connections[index]?.Dispose(); }
                    catch (Exception exception) { errors.Enqueue(exception); }
                }

                barrier.SignalAndWait(timeout.Token);
                if (duplicate || !errors.IsEmpty)
                    break;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var completed = false;
        try
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                // Only this test's pool, and only between waves, while every owner is closed: an empty
                // pool is what sends an open down the leak-reclaim path.
                SqliteConnection.ClearPool(pool);
                barrier.SignalAndWait(timeout.Token);
                barrier.SignalAndWait(timeout.Token);
                duplicate = connections.Select(c => c?.Handle).Distinct().Count() != workerCount;
                barrier.SignalAndWait(timeout.Token);
                barrier.SignalAndWait(timeout.Token);
                if (duplicate || !errors.IsEmpty)
                    break;
            }
            completed = true;
        }
        finally
        {
            if (!completed)
                timeout.Cancel();
            await Task.WhenAll(workers);
            SqliteConnection.ClearPool(pool);
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort on Windows */ }
        }

        Assert.Empty(errors);
        Assert.False(duplicate, "two open SqliteConnections were handed the same native connection");
    }
}
