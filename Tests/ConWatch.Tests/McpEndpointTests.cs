using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    private async Task<JsonElement> RpcAsync(string method, object? @params = null)
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
        var response = await _client.SendAsync(request);
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
    public async Task Lists_the_four_read_only_tools()
    {
        var init = await InitializeAsync();
        Assert.Equal("ConnectionWatcher", init.GetProperty("serverInfo").GetProperty("name").GetString());

        var tools = (await RpcAsync("tools/list")).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(4, tools.Count);
        Assert.Contains("get_current_status", tools);
        Assert.Contains("get_connection_report", tools);
        Assert.Contains("get_transitions", tools);
        Assert.Contains("get_glitches", tools);
    }

    [Fact]
    public async Task Status_and_report_tools_answer_from_the_real_database()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await _client.GetAsync("/api/v1/status");
            return status.IsSuccessStatusCode &&
                   (await status.Content.ReadAsStringAsync()).Contains("Healthy");
        });

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
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await _client.GetAsync("/api/v1/status");
            return status.IsSuccessStatusCode &&
                   (await status.Content.ReadAsStringAsync()).Contains("Healthy");
        });
        await InitializeAsync();

        var json = JsonSerializer.Deserialize<JsonElement>(ToolText(await RpcAsync("tools/call", new
        {
            name = "get_current_status",
            arguments = new { },
        })));

        Assert.True(json.GetProperty("monitoringEnabled").GetBoolean());
        Assert.True(json.GetProperty("verdict").TryGetProperty("operator", out _));
        var router = json.GetProperty("targets").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "Router");
        foreach (var key in new[] { "id", "address", "state", "assumed", "successRate", "avgRtt",
                     "sampleSize", "pingCount", "operator" })
            Assert.True(router.TryGetProperty(key, out _), $"missing '{key}'");
    }

    [Fact]
    public async Task Transitions_tool_speaks_the_rest_transition_shape()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await _client.GetAsync("/api/v1/status");
            return status.IsSuccessStatusCode &&
                   (await status.Content.ReadAsStringAsync()).Contains("Healthy");
        });
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
}
