using System.Net.Http.Json;
using System.Text.Json;
using ConWatch.NameResolution;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class DnsStatusTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;
    public DnsStatusTests() => _client = _factory.CreateClient();
    public void Dispose() => _factory.Dispose();

    /// <summary>One stateless MCP tools/call, the way McpEndpointTests does it; returns the tool's JSON text parsed.</summary>
    private async Task<JsonElement> CallToolAsync(string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name, arguments = new { } },
            }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').First(l => l.StartsWith("data:"))["data:".Length..].Trim();
        var text = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    [Fact]
    public async Task Status_and_MCP_carry_the_dns_block_and_metrics_carry_the_gauge()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");
        await _factory.Services.GetRequiredService<SystemResolverService>().ReconcileNowAsync(CancellationToken.None);

        // The built-in Resolver profile probes every 30 s and needs six samples before the summary leaves
        // Unknown; give the managed row the suite's fast profile (the document edit is the reconciler's path,
        // the API refuses a profile change on a managed row).
        await ConfigApiTests.Eventually(async () =>
        {
            var edited = await _factory.Services.GetRequiredService<ConPersistence.Config.ConfigService>()
                .EditDocumentAsync(doc =>
                {
                    var i = doc.Targets.FindIndex(t => t.SystemManaged);
                    if (i < 0) return ConPersistence.Config.ConfigResult.Fail(ConPersistence.Config.ConfigErrorKind.NotFound, "not yet");
                    doc.Targets[i] = doc.Targets[i] with { Profile = "Fast" };
                    return ConPersistence.Config.ConfigResult.Ok();
                });
            return edited.Success;
        });

        JsonElement dns = default;
        await ConfigApiTests.Eventually(async () =>
        {
            var response = await _client.GetAsync("/api/v1/status"); // 503 while the engine is (re)building
            if (!response.IsSuccessStatusCode) return false;
            var status = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!status.TryGetProperty("dns", out dns) || dns.ValueKind != JsonValueKind.Object) return false;
            return dns.GetProperty("systemResolvers").GetArrayLength() == 1;
        });
        Assert.True(dns.GetProperty("watched").GetBoolean());
        Assert.Contains(dns.GetProperty("state").GetString(), new[] { "Unknown", "Ok" }); // the fake answers every ping
        var resolver = dns.GetProperty("systemResolvers")[0];
        Assert.Equal("10.0.0.53", resolver.GetProperty("address").GetString());
        Assert.Equal("Your resolver 10.0.0.53", resolver.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, dns.GetProperty("blame").ValueKind);
        Assert.Equal(4, dns.GetProperty("lookupNames").GetArrayLength());
        Assert.Equal("dns.google", dns.GetProperty("lookupNames")[0].GetProperty("name").GetString());

        var mcp = await CallToolAsync("get_current_status");
        Assert.True(mcp.TryGetProperty("dns", out var mcpDns) && mcpDns.ValueKind == JsonValueKind.Object);

        // The gauge is absent while the summary is Unknown (no lookup results yet); wait for the first answers.
        var metrics = "";
        await ConfigApiTests.Eventually(async () =>
        {
            metrics = await _client.GetStringAsync("/metrics");
            return metrics.Contains("conwatch_dns_up ");
        });
        Assert.Contains("conwatch_dns_up 1", metrics);
        Assert.Contains("conwatch_verdict{state=\"DnsDown\"} 0", metrics);
    }

    private async Task<JsonElement?> DnsBlockAsync()
    {
        var response = await _client.GetAsync("/api/v1/status"); // 503 while the engine is (re)building
        if (!response.IsSuccessStatusCode) return null;
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        return status.TryGetProperty("dns", out var dns) && dns.ValueKind == JsonValueKind.Object ? dns : null;
    }

    private static List<string?> LookupNamesOf(JsonElement dns) =>
        dns.GetProperty("lookupNames").EnumerateArray().Select(n => n.GetProperty("name").GetString()).ToList();

    /// <summary>With no configured names, the names the resolver is asked for are the enabled hostname
    /// targets, exactly: a disabled one adds nothing.</summary>
    [Fact]
    public async Task Lookup_names_add_enabled_hostname_targets()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Dns.LookupNames"] = "[]" })).EnsureSuccessStatusCode();
        // The OS has a resolver, but with nothing to ask it the reconciler creates no watch for it.
        _factory.Resolvers.Set("192.0.2.53");
        var reconciler = _factory.Services.GetRequiredService<SystemResolverService>();
        await reconciler.ReconcileNowAsync(CancellationToken.None);
        var before = await _client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets");
        Assert.NotNull(before);
        Assert.DoesNotContain(before, t => t.SystemManaged);

        (await _client.PostAsJsonAsync("/api/v1/targets",
            new ConPersistence.Config.TargetDto { Name = "example.org", Address = "example.org", Parent = "Router", Profile = "Fast" }))
            .EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/v1/targets",
            new ConPersistence.Config.TargetDto { Name = "off.example", Address = "off.example", Parent = "Router", Profile = "Fast", Enabled = false }))
            .EnsureSuccessStatusCode();

        await ConfigApiTests.Eventually(async () =>
            await DnsBlockAsync() is { } d && d.GetProperty("watched").GetBoolean());
        var dns = (await DnsBlockAsync())!.Value;
        Assert.Equal(["example.org"], LookupNamesOf(dns));

        // Removing the only hostname target leaves nothing to ask for: the watch is off again.
        var targets = (await _client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets"))!;
        var gone = await _client.DeleteAsync($"/api/v1/targets/{targets.Single(t => t.Name == "example.org").Id}");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, gone.StatusCode);
        await ConfigApiTests.Eventually(async () =>
            await DnsBlockAsync() is { } d && !d.GetProperty("watched").GetBoolean());
        Assert.Empty(LookupNamesOf((await DnsBlockAsync())!.Value));
    }
}
