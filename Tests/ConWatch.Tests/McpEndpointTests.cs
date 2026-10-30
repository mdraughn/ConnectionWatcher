using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>
/// Drives the /mcp endpoint with raw JSON-RPC over stateless streamable HTTP:
/// initialize, list the read-only tools, and call them against a seeded node.
/// </summary>
public class McpEndpointTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;
    private int _id;

    public McpEndpointTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    /// <summary>Seeds the home config and waits for its chain of rebuilds to finish on a
    /// healthy engine. A warm rebuild can already read Healthy, and a tool call in the gap
    /// before the next one finds no engine (a status of nulls).</summary>
    private async Task SeedAndSettleAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await HostTestWait.SettledRebuildCountAsync(_factory.Services.GetRequiredService<WatcherHostService>());
        await HostTestWait.Eventually(() => HostTestWait.SteadyStateAsync(_client));
    }

    private Task<JsonElement> RpcAsync(string method, object? @params = null) =>
        RpcWithAsync(_client, method, @params);

    private async Task<JsonElement> RpcWithAsync(HttpClient client, string method, object? @params = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = ++_id,
                method,
                @params,
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            // Take the first data: line — stateless single-response stream.
            var data = body.Split('\n')
                .First(l => l.StartsWith("data:"))["data:".Length..].Trim();
            body = data;
        }
        var envelope = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.False(envelope.TryGetProperty("error", out var error),
            $"MCP error for {method}: {error}");
        return envelope.GetProperty("result");
    }

    private Task<JsonElement> InitializeAsync() => RpcAsync("initialize", new
    {
        protocolVersion = "2025-06-18",
        capabilities = new { },
        clientInfo = new { name = "conwatch-tests", version = "1.0" },
    });

    private static string ToolText(JsonElement callResult) =>
        callResult.GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task Lists_the_five_read_only_tools()
    {
        var init = await InitializeAsync();
        Assert.Equal("ConnectionWatcher", init.GetProperty("serverInfo").GetProperty("name").GetString());

        var tools = (await RpcAsync("tools/list")).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(5, tools.Count);
        Assert.Contains("list_discoveries", tools);
        Assert.Contains("get_current_status", tools);
        Assert.Contains("get_connection_report", tools);
        Assert.Contains("get_transitions", tools);
        Assert.Contains("get_glitches", tools);
    }

    [Fact]
    public async Task Status_and_report_tools_answer_from_the_real_database()
    {
        await SeedAndSettleAsync();
        await InitializeAsync();

        var status = ToolText(await RpcAsync("tools/call", new
        {
            name = "get_current_status",
            arguments = new { },
        }));
        Assert.Contains("\"monitoringEnabled\":true", status);
        Assert.Contains("Router", status);
        Assert.Contains("Healthy", status);

        var report = ToolText(await RpcAsync("tools/call", new
        {
            name = "get_connection_report",
            arguments = new
            {
                fromUtc = DateTime.UtcNow.AddHours(-1).ToString("o"),
                toUtc = DateTime.UtcNow.ToString("o"),
            },
        }));
        Assert.Contains("anchorUptimePercent", report);

        var transitions = ToolText(await RpcAsync("tools/call", new
        {
            name = "get_transitions",
            arguments = new { limit = 10 },
        }));
        Assert.Contains("Responding", transitions);
    }

    [Fact]
    public async Task Status_tool_reports_the_status_snapshot()
    {
        await SeedAndSettleAsync();
        await InitializeAsync();

        var json = JsonSerializer.Deserialize<JsonElement>(ToolText(await RpcAsync("tools/call", new
        {
            name = "get_current_status",
            arguments = new { },
        })));

        Assert.True(json.GetProperty("monitoringEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("warnings").ValueKind);
        Assert.True(json.GetProperty("verdict").TryGetProperty("operator", out _));
        var router = json.GetProperty("targets").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "Router");
        foreach (var key in new[] { "id", "address", "state", "assumed", "successRate", "avgRtt",
                     "sampleSize", "pingCount", "operator", "scope", "critical", "explainedBy" })
            Assert.True(router.TryGetProperty(key, out _), $"missing '{key}'");
        Assert.Equal(JsonValueKind.False, router.GetProperty("critical").ValueKind);
    }

    [Fact]
    public async Task Transitions_tool_speaks_the_rest_transition_shape()
    {
        await SeedAndSettleAsync();
        await _factory.Services.GetRequiredService<ConPersistence.Recording.TransitionRecorder>().FlushAsync();
        await InitializeAsync();

        var text = ToolText(await RpcAsync("tools/call", new
        {
            name = "get_transitions",
            arguments = new { limit = 10 },
        }));

        var first = JsonSerializer.Deserialize<JsonElement>(text)[0];
        foreach (var key in new[] { "id", "timestamp", "subjectId", "subjectName", "subjectKind", "from", "to", "trigger" })
            Assert.True(first.TryGetProperty(key, out _), $"missing '{key}'");
        Assert.Matches(@"\+00:00$", first.GetProperty("timestamp").GetString());
    }

    [Fact]
    public async Task Transitions_tool_filters_by_scope_and_tags_each_row()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NAS", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
        await ConfigApiTests.Eventually(async () =>
        {
            var body = await _client.GetStringAsync("/api/v1/transitions?limit=200");
            return body.Contains("\"NAS\"") && body.Contains("\"Router\"");
        });
        await InitializeAsync();

        var local = JsonSerializer.Deserialize<JsonElement>(ToolText(await RpcAsync("tools/call", new
        {
            name = "get_transitions",
            arguments = new { scope = "local", limit = 100 },
        })));
        Assert.NotEmpty(local.EnumerateArray());
        Assert.All(local.EnumerateArray(), i =>
        {
            Assert.Equal("NAS", i.GetProperty("subjectName").GetString());
            Assert.Equal("Local", i.GetProperty("scope").GetString());
        });

        var glitches = JsonSerializer.Deserialize<JsonElement>(ToolText(await RpcAsync("tools/call", new
        {
            name = "get_glitches",
            arguments = new { scope = "local" },
        })));
        Assert.Equal(JsonValueKind.Array, glitches.ValueKind);
    }

    [Fact]
    public async Task A_bad_scope_is_a_tool_error_not_a_server_failure()
    {
        await InitializeAsync();
        foreach (var tool in new[] { "get_transitions", "get_glitches" })
        {
            var result = await RpcAsync("tools/call", new { name = tool, arguments = new { scope = "bogus" } });
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Contains("scope must be", ToolText(result));
        }
    }

    private async Task<List<JsonElement>> ListDiscoveriesAsync(object arguments)
    {
        var text = ToolText(await RpcAsync("tools/call", new { name = "list_discoveries", arguments }));
        return JsonSerializer.Deserialize<JsonElement>(text).EnumerateArray().ToList();
    }

    [Fact]
    public async Task list_discoveries_returns_the_inbox_and_filters_by_status()
    {
        await InitializeAsync();
        (await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new object[]
            {
                new { address = "192.0.2.1", kind = "Gateway", method = "DefaultRoute", origin = "server", evidence = "e1" },
                new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
            },
        })).EnsureSuccessStatusCode();
        var all = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/discoveries"));
        var dismissId = all.EnumerateArray().Single(d => d.GetProperty("address").GetString() == "192.0.2.50")
            .GetProperty("id").GetString();
        (await _client.PostAsync($"/api/v1/discoveries/{dismissId}/dismiss", null)).EnsureSuccessStatusCode();

        var newOnes = await ListDiscoveriesAsync(new { });
        var dismissed = await ListDiscoveriesAsync(new { status = "dismissed" });
        var everything = await ListDiscoveriesAsync(new { status = "all" });
        var hidden = await ListDiscoveriesAsync(new { status = "Hidden" });

        Assert.Equal(["192.0.2.1"], newOnes.Select(d => d.GetProperty("address").GetString()));
        Assert.Equal("Gateway", newOnes[0].GetProperty("kind").GetString());
        Assert.Equal("server", newOnes[0].GetProperty("origin").GetString());
        Assert.Equal(["192.0.2.50"], dismissed.Select(d => d.GetProperty("address").GetString()));
        Assert.Equal(["192.0.2.50"], hidden.Select(d => d.GetProperty("address").GetString()));
        Assert.Equal(2, everything.Count);
    }

    [Fact]
    public async Task list_discoveries_with_a_bad_status_is_a_readable_tool_error()
    {
        await InitializeAsync();

        var result = await RpcAsync("tools/call", new { name = "list_discoveries", arguments = new { status = "bogus" } });

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("status must be", ToolText(result));
    }

    private async Task SeedOneDiscoveryAsync() =>
        (await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new[] { new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" } },
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task list_discoveries_is_open_while_no_admin_password_is_set()
    {
        await SeedOneDiscoveryAsync();
        await InitializeAsync();

        var result = await RpcAsync("tools/call", new { name = "list_discoveries", arguments = new { } });

        Assert.False(result.TryGetProperty("isError", out var e) && e.GetBoolean());
        Assert.Contains("192.0.2.50", ToolText(result));
    }

    [Fact]
    public async Task list_discoveries_needs_admin_mode_once_a_password_is_set_exactly_like_the_page()
    {
        await SeedOneDiscoveryAsync();
        // set-password leaves _client signed in as admin; a second client has no cookie.
        (await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var anonymous = _factory.CreateClient();

        var denied = await RpcWithAsync(anonymous, "tools/call", new { name = "list_discoveries", arguments = new { } });
        var allowed = await RpcWithAsync(_client, "tools/call", new { name = "list_discoveries", arguments = new { } });

        Assert.True(denied.GetProperty("isError").GetBoolean());
        Assert.Contains("admin", ToolText(denied), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.50", ToolText(denied));        // nothing leaks through the error
        Assert.Contains("192.0.2.50", ToolText(allowed));

        // The watching tools stay open to the same anonymous client.
        var status = await RpcWithAsync(anonymous, "tools/call", new { name = "get_current_status", arguments = new { } });
        Assert.False(status.TryGetProperty("isError", out var e) && e.GetBoolean());
    }

    [Fact]
    public async Task The_tool_is_still_listed_for_everyone()
    {
        (await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var anonymous = _factory.CreateClient();
        await RpcWithAsync(anonymous, "initialize", new
        {
            protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" },
        });
        var tools = (await RpcWithAsync(anonymous, "tools/list")).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("list_discoveries", tools);
    }

    [Fact]
    public async Task Every_listed_tool_is_annotated_read_only()
    {
        await InitializeAsync();

        var tools = (await RpcAsync("tools/list")).GetProperty("tools").EnumerateArray().ToList();

        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString();
            Assert.True(tool.TryGetProperty("annotations", out var annotations), $"{name} has no annotations");
            Assert.True(annotations.TryGetProperty("readOnlyHint", out var hint) && hint.ValueKind == JsonValueKind.True,
                $"{name} is not annotated readOnlyHint=true");
        }
    }

    [Fact]
    public async Task get_glitches_rows_keep_their_shape_and_utc_timestamps()
    {
        await SeedAndSettleAsync();
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConPersistence.ConWatchDbContext>>();
        var t0 = new DateTime(2026, 9, 4, 3, 34, 0, DateTimeKind.Utc);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var googleId = await db.Targets.Where(t => t.Name == "Google").Select(t => t.Id).SingleAsync();
            db.Glitches.AddRange(
                new ConPersistence.Entities.GlitchRow { TargetId = googleId, TargetName = "Google", Class = 0,
                    StartUtc = t0, EndUtc = t0.AddSeconds(9), LostCount = 2, SampleCount = 8, Detail = "2 of 8 lost" },
                new ConPersistence.Entities.GlitchRow { TargetId = ConEngine.Topology.VerdictCoordinator.SubjectId,
                    TargetName = "Internet", Class = 1, StartUtc = t0.AddMinutes(1), EndUtc = t0.AddMinutes(1).AddSeconds(70),
                    Detail = "anchors: Google, Quad9" },
                new ConPersistence.Entities.GlitchRow { TargetId = Guid.NewGuid(), TargetName = "Gone", Class = 0,
                    StartUtc = t0.AddMinutes(2), EndUtc = t0.AddMinutes(2).AddSeconds(3), LostCount = 1, SampleCount = 5 });
            await db.SaveChangesAsync();
        }
        await InitializeAsync();

        var rows = JsonSerializer.Deserialize<JsonElement>(ToolText(await RpcAsync("tools/call", new
        {
            name = "get_glitches",
            arguments = new { fromUtc = "2026-09-04T00:00:00Z", toUtc = "2026-09-05T00:00:00Z" },
        }))).EnumerateArray().ToList();

        // Newest first; every row has exactly these keys.
        Assert.Equal(["Gone", "Internet", "Google"], rows.Select(r => r.GetProperty("targetName").GetString()));
        foreach (var row in rows)
            Assert.Equal(["id", "targetId", "targetName", "class", "startUtc", "endUtc", "lostCount", "sampleCount", "detail", "scope"],
                row.EnumerateObject().Select(p => p.Name));
        var google = rows[2];
        Assert.Equal("Target", google.GetProperty("class").GetString());
        Assert.Equal("Verdict", google.GetProperty("scope").GetString());
        Assert.Equal(2, google.GetProperty("lostCount").GetInt32());
        Assert.Equal(8, google.GetProperty("sampleCount").GetInt32());
        Assert.Equal("2 of 8 lost", google.GetProperty("detail").GetString());
        Assert.Equal("Internet", rows[1].GetProperty("class").GetString());
        Assert.Equal("Verdict", rows[1].GetProperty("scope").GetString());
        Assert.Equal("Unknown", rows[0].GetProperty("scope").GetString());     // a target since deleted
        // Timestamps are UTC-marked ISO-8601 and round-trip to the seeded instant.
        // The same DateTimeOffset spelling as the REST glitches endpoint.
        foreach (var row in rows)
        {
            Assert.EndsWith("+00:00", row.GetProperty("startUtc").GetString());
            Assert.EndsWith("+00:00", row.GetProperty("endUtc").GetString());
        }
        Assert.Equal(t0, DateTime.Parse(google.GetProperty("startUtc").GetString()!, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal));
    }

    [Fact]
    public async Task get_glitches_rows_have_the_REST_shape()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConPersistence.ConWatchDbContext>>();
        var t0 = DateTime.Parse("2026-09-04T03:34:00Z").ToUniversalTime();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Glitches.AddRange(
                new ConPersistence.Entities.GlitchRow { TargetId = Guid.NewGuid(), TargetName = "Google", Class = 0,
                    StartUtc = t0, EndUtc = t0.AddSeconds(9), LostCount = 2, SampleCount = 8 },
                new ConPersistence.Entities.GlitchRow { TargetId = ConEngine.Topology.VerdictCoordinator.SubjectId,
                    TargetName = "Internet", Class = 1, StartUtc = t0, EndUtc = t0.AddSeconds(70), Detail = "anchors" });
            await db.SaveChangesAsync();
        }
        await InitializeAsync();

        var mcp = JsonSerializer.Deserialize<JsonElement>(ToolText(await RpcAsync("tools/call", new
        {
            name = "get_glitches",
            arguments = new { fromUtc = "2026-09-04T00:00:00Z", toUtc = "2026-09-05T00:00:00Z" },
        }))).EnumerateArray().ToList();
        var rest = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/glitches"))
            .GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(rest.Count, mcp.Count);
        for (var i = 0; i < rest.Count; i++)
        {
            Assert.Equal(rest[i].EnumerateObject().Select(p => p.Name).Order(),
                mcp[i].EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(rest[i].GetProperty("startUtc").GetString(), mcp[i].GetProperty("startUtc").GetString());
            Assert.EndsWith("+00:00", mcp[i].GetProperty("startUtc").GetString());
        }
    }
}
