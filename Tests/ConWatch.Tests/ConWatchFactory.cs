using ConCore;
using ConEngine.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConWatch.Tests;

/// <summary>
/// Boots the real ConWatch app on a temp SQLite file with the fake ping
/// transceiver and instant rebuild debounce.
/// </summary>
public class ConWatchFactory : WebApplicationFactory<Program>
{
    public FakePingTransceiverFactory Pings { get; } = new();
    public string DbPath { get; }
    private readonly string? _ownedDir;

    /// <summary>Extra test-only service configuration (e.g. routing the
    /// clone HttpClient at another in-memory app). Set before CreateClient.</summary>
    public Action<IServiceCollection>? TestServices { get; set; }

    public ConWatchFactory(string? dbPath = null)
    {
        if (dbPath is null)
        {
            // Own directory per instance: the app puts sibling files beside
            // the database (admin-password.txt, Data Protection keys), and a
            // shared temp dir would leak them between parallel tests.
            _ownedDir = Directory.CreateTempSubdirectory("conwatch-test").FullName;
            DbPath = Path.Combine(_ownedDir, "conwatch.db");
        }
        else
        {
            DbPath = dbPath;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Persistence:DbPath", DbPath);
        builder.UseSetting("Persistence:RebuildDebounceSeconds", "0");
        builder.UseSetting("Verdict:AnchorPool", "Internet");
        builder.UseSetting("Verdict:WeatherThreshold", "2");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPingTransceiverFactory>();
            services.AddSingleton<IPingTransceiverFactory>(Pings);
            TestServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // Release pooled SQLite handles: without this, deletes of just-used
        // databases fail on Windows (file locked) and REOPENS go wrong on
        // Linux (pooled connections still bound to an old unlinked inode).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            // Only ever delete what this factory created. A caller-supplied
            // DbPath is the CALLER's file — restart tests reopen it with a
            // second factory. The old delete-it-anyway only "worked" on
            // Windows because open handles made it throw; on Linux the
            // unlink succeeded and the second factory booted onto a ghost.
            if (_ownedDir is not null)
                Directory.Delete(_ownedDir, recursive: true);
        }
        catch (IOException)
        {
            // Temp file cleanup is best-effort.
        }
    }
}
