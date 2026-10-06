using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Discovery;
using ConInfrastructure.Discovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConWatch.Tests;

public class DiscoveryApiTests : IDisposable
{
    /// <summary>Scripted candidates; the edge search can be held open to test the busy gate.</summary>
    private sealed class FakeScanner : IDiscoveryScanner
    {
        public static List<DiscoveryCandidate> Router { get; } =
        [
            new("192.0.2.1", null, DiscoveryKind.Gateway, DiscoveryMethod.DefaultRoute, "answers pings in 1 ms"),
        ];

        public static TaskCompletionSource<bool>? HoldEdge { get; set; }
        public static TaskCompletionSource<bool> EdgeStarted { get; set; } = new();

        public Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveryCandidate>>(Router);

        public Task<LanScanResult> ScanLanAsync(CancellationToken ct) =>
            Task.FromResult(new LanScanResult([], true));

        public async Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct)
        {
            EdgeStarted.TrySetResult(true);
            if (HoldEdge is { } hold)
                await hold.Task.WaitAsync(ct);
            return [new("203.0.113.5", null, DiscoveryKind.NetworkEdge, DiscoveryMethod.Traceroute, $"hop 3 toward {host}")];
        }
    }

    private sealed class Factory : ConWatchFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDiscoveryScanner>();
                services.AddSingleton<IDiscoveryScanner, FakeScanner>();
            });
        }
    }

    private readonly Factory _factory = new();
    private readonly HttpClient _client;

    public DiscoveryApiTests()
    {
        FakeScanner.HoldEdge = null;
        FakeScanner.EdgeStarted = new();
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static List<JsonElement> Items(JsonElement array) => array.EnumerateArray().ToList();

    [Fact]
    public async Task Router_search_stores_candidates_as_New_and_a_rescan_updates_instead_of_duplicating()
    {
        var first = await _client.PostAsync("/api/v1/discoveries/search/router", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await first.Content.ReadAsStringAsync());
        Assert.Equal(1, body.GetProperty("found").GetInt32());
        Assert.Equal(1, body.GetProperty("created").GetInt32());

        var list = Items(await GetJson("/api/v1/discoveries"));
        var row = Assert.Single(list);
        Assert.Equal("192.0.2.1", row.GetProperty("address").GetString());
        Assert.Equal("Gateway", row.GetProperty("kind").GetString());
        Assert.Equal("DefaultRoute", row.GetProperty("method").GetString());
        Assert.Equal("server", row.GetProperty("origin").GetString());
        Assert.Equal("New", row.GetProperty("status").GetString());

        var second = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsync("/api/v1/discoveries/search/router", null)).Content.ReadAsStringAsync());
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(1, second.GetProperty("updated").GetInt32());
        Assert.Single(Items(await GetJson("/api/v1/discoveries?status=all")));
    }

    [Fact]
    public async Task Dismiss_hides_it_from_the_default_view_and_rediscovery_does_not_resurrect_it()
    {
        await _client.PostAsync("/api/v1/discoveries/search/router", null);
        var id = Items(await GetJson("/api/v1/discoveries")).Single().GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/api/v1/discoveries/{id}/dismiss", null)).StatusCode);
        Assert.Empty(Items(await GetJson("/api/v1/discoveries")));
        Assert.Single(Items(await GetJson("/api/v1/discoveries?status=dismissed")));

        await _client.PostAsync("/api/v1/discoveries/search/router", null);   // found again
        Assert.Empty(Items(await GetJson("/api/v1/discoveries")));
        Assert.Single(Items(await GetJson("/api/v1/discoveries?status=dismissed")));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/api/v1/discoveries/{id}/restore", null)).StatusCode);
        Assert.Single(Items(await GetJson("/api/v1/discoveries")));
    }

    [Fact]
    public async Task Restore_all_dismissed_reports_how_many()
    {
        await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new[]
            {
                new { address = "192.0.2.10", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
                new { address = "192.0.2.11", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
            },
        });
        foreach (var d in Items(await GetJson("/api/v1/discoveries")))
            await _client.PostAsync($"/api/v1/discoveries/{d.GetProperty("id").GetString()}/dismiss", null);

        var restored = JsonSerializer.Deserialize<JsonElement>(await (await _client
            .PostAsync("/api/v1/discoveries/restore-dismissed", null)).Content.ReadAsStringAsync());

        Assert.Equal(2, restored.GetProperty("restored").GetInt32());
        Assert.Equal(2, Items(await GetJson("/api/v1/discoveries")).Count);
    }

    [Fact]
    public async Task Unknown_ids_are_404_and_a_bad_status_is_400()
    {
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/v1/discoveries/{missing}/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/v1/discoveries/{missing}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/discoveries?status=bogus")).StatusCode);
    }

    [Fact]
    public async Task A_batch_post_stores_valid_items_and_reports_created_and_updated()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new object[]
            {
                new { address = "192.0.2.50", hostname = "nas.example.com", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC", evidence = "aa:bb:cc vendor" },
                new { address = "192.0.2.1", kind = "Gateway", method = "DefaultRoute", origin = "tray:PC" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.GetProperty("created").GetInt32());

        var again = JsonSerializer.Deserialize<JsonElement>(await (await _client.PostAsJsonAsync("/api/v1/discoveries",
            new { items = new[] { new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" } } }))
            .Content.ReadAsStringAsync());
        Assert.Equal(1, again.GetProperty("updated").GetInt32());
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"items":[{"address":"router.example.com","kind":"Gateway","method":"DefaultRoute","origin":"x"}]}""")]
    [InlineData("""{"items":[{"address":"127.0.0.1","kind":"Gateway","method":"DefaultRoute","origin":"x"}]}""")]
    [InlineData("""{"items":[{"address":"192.0.2.1","kind":"Gateway","method":"DefaultRoute"}]}""")]
    [InlineData("""{"items":[{"address":"192.0.2.1","kind":9,"method":"DefaultRoute","origin":"x"}]}""")]
    [InlineData("""{"items":[{"address":"192.0.2.1","kind":"Bogus","method":"DefaultRoute","origin":"x"}]}""")]
    public async Task A_bad_batch_is_a_400_and_writes_nothing(string json)
    {
        var response = await _client.PostAsync("/api/v1/discoveries",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Items(await GetJson("/api/v1/discoveries?status=all")));
    }

    [Fact]
    public async Task Adopting_via_POST_targets_marks_the_discovery_and_a_failed_adoption_does_not()
    {
        await ConfigApiTests.SeedHomeAsync(_client);                       // Router 192.168.1.1 etc.
        await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new[]
            {
                new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
                new { address = "192.168.1.1", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
            },
        });
        var byAddress = Items(await GetJson("/api/v1/discoveries"))
            .ToDictionary(d => d.GetProperty("address").GetString()!, d => d.GetProperty("id").GetString()!);

        // A target on an address that already exists fails validation: the discovery stays New.
        var clash = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={byAddress["192.168.1.1"]}",
            new { name = "Dup", address = "192.168.1.1", profile = "Fast", scope = "Local" });
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        Assert.Contains(Items(await GetJson("/api/v1/discoveries")), d => d.GetProperty("address").GetString() == "192.168.1.1");

        var ok = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={byAddress["192.0.2.50"]}",
            new { name = "NasBox", address = "192.0.2.50", profile = "Fast", scope = "Local" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var adopted = Assert.Single(Items(await GetJson("/api/v1/discoveries?status=adopted")));
        Assert.Equal("NasBox", adopted.GetProperty("adoptedTargetName").GetString());

        // Adopting it again while the target exists is a conflict; an unknown id is a 404.
        var again = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={byAddress["192.0.2.50"]}",
            new { name = "NasBox2", address = "192.0.2.51", profile = "Fast", scope = "Local" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var unknown = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={Guid.NewGuid()}",
            new { name = "X", address = "192.0.2.52", profile = "Fast" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_adopted_target_leaves_the_discovery_Adopted_without_a_name_and_it_can_be_dismissed()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new[] { new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" } },
        });
        var id = Items(await GetJson("/api/v1/discoveries")).Single().GetProperty("id").GetString();
        var created = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={id}",
            new { name = "NasBox", address = "192.0.2.50", profile = "Fast", scope = "Local" });
        var targetId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString();

        // Live adoption: dismiss is a 409.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/v1/discoveries/{id}/dismiss", null)).StatusCode);

        (await _client.DeleteAsync($"/api/v1/targets/{targetId}")).EnsureSuccessStatusCode();
        var row = Assert.Single(Items(await GetJson("/api/v1/discoveries?status=adopted")));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("adoptedTargetName").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/api/v1/discoveries/{id}/dismiss", null)).StatusCode);
    }

    [Fact]
    public async Task A_second_search_while_one_is_running_is_a_409_and_the_first_still_stores_its_result()
    {
        FakeScanner.HoldEdge = new TaskCompletionSource<bool>();
        var running = _client.PostAsync("/api/v1/discoveries/search/edge", null);
        await FakeScanner.EdgeStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));   // it holds the gate now

        var busy = await _client.PostAsync("/api/v1/discoveries/search/router", null);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains("search-busy", await busy.Content.ReadAsStringAsync());

        FakeScanner.HoldEdge.SetResult(true);
        var finished = await running;
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        var edge = Assert.Single(Items(await GetJson("/api/v1/discoveries")));
        Assert.Equal("NetworkEdge", edge.GetProperty("kind").GetString());
        Assert.Contains("1.1.1.1", edge.GetProperty("evidence").GetString());   // the default host
    }

    [Fact]
    public async Task Every_discovery_route_is_locked_when_an_admin_password_is_set()
    {
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        var anon = _factory.CreateClient();
        var id = Guid.NewGuid();

        foreach (var (method, url) in new[]
        {
            ("GET", "/api/v1/discoveries"), ("POST", "/api/v1/discoveries"),
            ("POST", "/api/v1/discoveries/search/router"), ("POST", "/api/v1/discoveries/search/edge"),
            ("POST", "/api/v1/discoveries/restore-dismissed"),
            ("POST", $"/api/v1/discoveries/{id}/dismiss"), ("POST", $"/api/v1/discoveries/{id}/restore"),
        })
        {
            var response = await anon.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = method == "POST" ? JsonContent.Create(new { }) : null,
            });
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {url} was {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Concurrent_identical_posts_end_as_one_row_with_no_failures()
    {
        var body = new
        {
            items = new[] { new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" } },
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => _client.PostAsJsonAsync("/api/v1/discoveries", body)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var counts = await Task.WhenAll(responses.Select(async r =>
            JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync())));
        Assert.Equal(1, counts.Sum(c => c.GetProperty("created").GetInt32()));
        Assert.Equal(11, counts.Sum(c => c.GetProperty("updated").GetInt32()));
        Assert.Single(Items(await GetJson("/api/v1/discoveries?status=all")));
    }
}
