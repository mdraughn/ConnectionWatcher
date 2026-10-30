using System.Net.Http.Json;
using ConWatch.NameResolution;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class PoolsPageTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public PoolsPageTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_pools_editor_offers_only_tree_targets()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");
        await _factory.Services.GetRequiredService<SystemResolverService>().ReconcileNowAsync(CancellationToken.None);
        var targets = await _client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets");
        Assert.Single(targets!, t => t.SystemManaged && t.Address == "10.0.0.53"); // the resolver target exists
        var html = await _client.GetStringAsync("/Config/Pools");
        Assert.Contains("<option>Router</option>", html);                         // a tree target is offered
        Assert.DoesNotContain("Your resolver 10.0.0.53", html);  // a Resolver is not (the validator rejects it)
    }
}
