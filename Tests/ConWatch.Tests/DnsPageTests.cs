using System.Net.Http.Json;
using ConWatch.NameResolution;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class DnsPageTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public DnsPageTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task SeedWithResolverAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");
        await _factory.Services.GetRequiredService<SystemResolverService>().ReconcileNowAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_fallback_dashboard_lists_resolvers_under_the_Name_resolution_heading_not_in_the_tree()
    {
        await SeedWithResolverAsync();
        string html = "";
        await ConfigApiTests.Eventually(async () =>
        {
            html = await _client.GetStringAsync("/");
            return html.Contains("Name resolution") && html.Contains("Your resolver 10.0.0.53");
        });
        var fallback = html[html.IndexOf("id=\"dashboard-root\"", StringComparison.Ordinal)..];
        var heading = fallback.IndexOf("Name resolution", StringComparison.Ordinal);
        var tree = fallback.IndexOf("<ul class=\"cw-tree\">", StringComparison.Ordinal);
        Assert.True(tree >= 0 && tree < heading, "the verdict tree comes first");
        Assert.True(fallback.IndexOf("Your resolver 10.0.0.53", StringComparison.Ordinal) > heading,
            "the resolver is listed under the heading, never as a tree root");
    }
}
