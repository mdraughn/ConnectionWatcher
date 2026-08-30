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
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (_ownedDir is not null)
                Directory.Delete(_ownedDir, recursive: true);
            else if (File.Exists(DbPath))
                File.Delete(DbPath);
        }
        catch (IOException)
        {
            // Temp file cleanup is best-effort.
        }
    }
}
