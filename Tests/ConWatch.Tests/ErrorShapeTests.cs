using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>Every error body is { "errors": [...] }, plus "code" only where a
/// client branches on it (the clone and take-over flows).</summary>
public class ErrorShapeTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public ErrorShapeTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static async Task AssertErrorBody(HttpResponseMessage response, HttpStatusCode status,
        string? code, params string[] errors)
    {
        Assert.Equal(status, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        string[] expectedKeys = code is null ? ["errors"] : ["code", "errors"];
        Assert.Equal(expectedKeys, body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(errors, body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray());
        if (code is not null)
            Assert.Equal(code, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bad_request_is_errors_only() =>
        await AssertErrorBody(await _client.GetAsync("/api/v1/export/transitions?format=xml"),
            HttpStatusCode.BadRequest, null, "format must be 'csv' or 'json'.");

    [Fact]
    public async Task Config_not_found_is_errors_only()
    {
        var id = Guid.NewGuid();
        await AssertErrorBody(await _client.DeleteAsync($"/api/v1/targets/{id}"),
            HttpStatusCode.NotFound, null, $"No target with id {id}.");
    }

    [Fact]
    public async Task Clone_failure_carries_its_code() =>
        await AssertErrorBody(
            await _client.PostAsJsonAsync("/api/v1/setup/clone", new { sourceUrl = "not a url" }),
            HttpStatusCode.BadRequest, "bad-source-url",
            "The source must be an absolute http(s) URL, e.g. https://conwatch.example.com/ " +
            "or http://192.168.1.5:8080.");

    [Fact]
    public async Task Status_without_an_engine_uses_the_same_shape()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(() => Task.FromResult(host.Engine is not null));

        // Straight to the database, bypassing the validating API, so the next
        // rebuild refuses to start an engine while the node stays enabled.
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var profile = await db.Profiles.SingleAsync(p => p.Name == "Fast");
            profile.RespondingThreshold = 0.5;
            profile.FalteringThreshold = 0.75;
            await db.SaveChangesAsync();
        }
        Assert.False(await host.RebuildAsync());

        await AssertErrorBody(await _client.GetAsync("/api/v1/status"),
            HttpStatusCode.ServiceUnavailable, null, "Engine is not running");
    }
}
