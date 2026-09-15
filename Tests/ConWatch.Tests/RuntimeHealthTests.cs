using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>The persistent-refusal warning: a TCP probe that never once got
/// accepted is a politeness problem the config-only ConfigHealth can't see
/// (it needs live counters from the running engine).</summary>
public class RuntimeHealthTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public RuntimeHealthTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Persistently_refused_monitor_produces_a_tcp_refused_warning()
    {
        await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Modem", address = "192.168.1.254", profile = "External", probe = "tcp:80",
        });

        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(async () =>
        {
            await Task.CompletedTask;
            return host.Engine is not null && host.Engine.Monitors.Values.Any(m => m.Target.Name == "Modem");
        });

        var engine = host.Engine!;
        var monitor = engine.Monitors.Values.Single(m => m.Target.Name == "Modem");
        for (int i = 0; i < 200; i++)
            monitor.RecordPingResult(new ConCore.PingResult(IPAddress.Parse("192.168.1.254"), true, 2, "Refused"));

        Assert.True(monitor.PersistentlyRefused);

        var warnings = RuntimeHealth.RefusalWarnings(engine);
        var warning = Assert.Single(warnings, w => w.Code == "tcp-refused");
        Assert.Contains("Modem", warning.Message);
        Assert.Contains("tcp:80", warning.Message);
    }

    [Fact]
    public async Task No_warning_when_engine_is_absent_or_has_no_refusals()
    {
        Assert.Empty(RuntimeHealth.RefusalWarnings(null));

        var response = await _client.GetAsync("/api/v1/config/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Config_health_endpoint_concatenates_runtime_refusal_warnings()
    {
        await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Modem", address = "192.168.1.254", profile = "External", probe = "tcp:80",
        });

        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(async () =>
        {
            await Task.CompletedTask;
            return host.Engine is not null && host.Engine.Monitors.Values.Any(m => m.Target.Name == "Modem");
        });

        var monitor = host.Engine!.Monitors.Values.Single(m => m.Target.Name == "Modem");
        for (int i = 0; i < 200; i++)
            monitor.RecordPingResult(new ConCore.PingResult(IPAddress.Parse("192.168.1.254"), true, 2, "Refused"));

        var response = await _client.GetAsync("/api/v1/config/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(doc.RootElement.GetProperty("warnings").EnumerateArray(),
            w => w.GetProperty("code").GetString() == "tcp-refused");
    }
}
