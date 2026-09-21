using System.Net;
using System.Net.Sockets;
using ConWatch;

namespace ConWatchCli.Tests;

public sealed class HostSmokeTests
{
    [Fact]
    public async Task ConWatchApp_serves_health_and_the_dashboard_from_a_pinned_content_root()
    {
        var dir = Directory.CreateTempSubdirectory("conwatch-smoke").FullName;
        var port = FreePort();
        using var cts = new CancellationTokenSource();
        var run = ConWatchApp.RunAsync(
            ["--urls", $"http://127.0.0.1:{port}", "--Persistence:DbPath", Path.Combine(dir, "conwatch.db")],
            pinContentRoot: true, ct: cts.Token);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            HttpResponseMessage? health = null;
            for (var i = 0; i < 60 && health is null; i++)
            {
                try { health = await http.GetAsync("/api/v1/health"); }
                catch (HttpRequestException) { await Task.Delay(500); }
            }
            Assert.NotNull(health);
            Assert.Equal(HttpStatusCode.OK, health!.StatusCode);

            var home = await http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            Assert.Contains("ConnectionWatcher", await home.Content.ReadAsStringAsync());
        }
        finally
        {
            cts.Cancel();
            await run;
            // Microsoft.Data.Sqlite pools native connections at the process level; the pool
            // keeps the db/wal file handle open after the host's own DbContext scopes are
            // disposed, which races a plain Directory.Delete on Windows.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
