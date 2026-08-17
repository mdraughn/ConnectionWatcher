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
    public string DbPath { get; } =
        Path.Combine(Path.GetTempPath(), $"conwatch-test-{Guid.NewGuid():N}.db");

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
            if (File.Exists(DbPath))
                File.Delete(DbPath);
        }
        catch (IOException)
        {
            // Temp file cleanup is best-effort.
        }
    }
}
