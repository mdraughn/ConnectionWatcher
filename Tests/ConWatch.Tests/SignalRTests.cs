using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace ConWatch.Tests;

public class SignalRTests : IAsyncLifetime
{
    private static readonly IPAddress RouterIp = IPAddress.Parse("192.168.1.1");
    private readonly ConWatchFactory _factory = new();
    private HttpClient _client = null!;
    private HubConnection _hub = null!;
    private readonly List<JsonElement> _transitions = new();
    private readonly List<(DateTime At, JsonElement Snapshot)> _snapshots = new();

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/status"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();
        _hub.On<JsonElement>("transition", t => { lock (_transitions) _transitions.Add(t); });
        _hub.On<JsonElement>("statusChanged", s => { lock (_snapshots) _snapshots.Add((DateTime.UtcNow, s)); });
        await _hub.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        _factory.Dispose();
    }

    [Fact]
    public async Task Outage_pushes_transition_and_snapshot_over_hub()
    {
        await ConfigApiTests.SeedHomeAsync(_client);

        _factory.Pings.SetUp(RouterIp, false);

        await ConfigApiTests.Eventually(() =>
        {
            lock (_transitions)
            {
                return Task.FromResult(_transitions.Any(t =>
                    t.GetProperty("subjectName").GetString() == "Router" &&
                    t.GetProperty("to").GetString() == "Down"));
            }
        });

        await ConfigApiTests.Eventually(() =>
        {
            lock (_snapshots)
            {
                return Task.FromResult(_snapshots.Any(s =>
                    s.Snapshot.ValueKind == JsonValueKind.Object &&
                    s.Snapshot.GetProperty("targets").EnumerateArray().Any(t =>
                        t.GetProperty("name").GetString() == "Router" &&
                        t.GetProperty("state").GetString() == "Down")));
            }
        });
    }

    [Fact]
    public async Task Snapshots_arrive_on_cadence_with_minimum_spacing()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        lock (_snapshots) _snapshots.Clear();

        await Task.Delay(3000);

        List<(DateTime At, JsonElement Snapshot)> seen;
        lock (_snapshots) seen = _snapshots.ToList();
        Assert.True(seen.Count >= 2, $"Expected >=2 snapshots in 3s, saw {seen.Count}");
        for (int i = 1; i < seen.Count; i++)
        {
            var gap = seen[i].At - seen[i - 1].At;
            Assert.True(gap >= TimeSpan.FromMilliseconds(200),
                $"Snapshots {i - 1}->{i} only {gap.TotalMilliseconds:F0}ms apart");
        }
    }

    [Fact]
    public async Task Disabling_the_engine_pushes_one_disabled_snapshot_and_then_no_live_ones()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(() =>
        {
            lock (_snapshots)
                return Task.FromResult(_snapshots.Any(s => s.Snapshot.GetProperty("targets").GetArrayLength() > 0));
        });
        lock (_snapshots)
            Assert.All(_snapshots.Where(s => s.Snapshot.GetProperty("targets").GetArrayLength() > 0),
                s => Assert.True(s.Snapshot.GetProperty("enabled").GetBoolean()));

        var off = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);

        await ConfigApiTests.Eventually(() =>
        {
            lock (_snapshots)
                return Task.FromResult(_snapshots.Any(s =>
                    s.Snapshot.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False));
        });

        // After the disabled snapshot nothing live-looking may follow.
        DateTime disabledAt;
        lock (_snapshots)
            disabledAt = _snapshots.First(s =>
                s.Snapshot.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False).At;
        await Task.Delay(2500); // more than two broadcaster cadences
        lock (_snapshots)
        {
            var after = _snapshots.Where(s => s.At > disabledAt).ToList();
            Assert.DoesNotContain(after, s =>
                !s.Snapshot.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False);
        }
    }
}
