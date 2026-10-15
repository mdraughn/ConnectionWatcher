using System.Net;
using System.Net.Http.Json;
using ConCore.Targets;
using ConPersistence.Config;
using ConWatch.NameResolution;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>The managed resolver target appears within a second of first boot; it must not hide the wizard.</summary>
public class FirstRunWithResolverTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public FirstRunWithResolverTests() => _client = _factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public void Dispose() => _factory.Dispose();

    private async Task<List<TargetDto>> TargetsAsync() =>
        (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;

    private async Task AddManagedResolverAsync(string address)
    {
        _factory.Resolvers.Set(address);
        await _factory.Services.GetRequiredService<SystemResolverService>().ReconcileNowAsync(CancellationToken.None);
        await ConfigApiTests.Eventually(async () => (await TargetsAsync()).Any(t => t.SystemManaged));
    }

    [Fact]
    public async Task A_fresh_install_with_only_a_managed_resolver_still_goes_to_the_wizard()
    {
        await AddManagedResolverAsync("10.0.0.53");

        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Setup", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_wizard_does_not_reuse_the_routers_managed_resolver_as_the_router()
    {
        await AddManagedResolverAsync("10.0.0.1");

        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(new SetupTarget("Router", "10.0.0.1"), null, ["8.8.8.8", "1.1.1.1"]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var targets = await TargetsAsync();
        var managed = Assert.Single(targets, t => t.SystemManaged);
        var router = Assert.Single(targets, t => t.Name == "Router");
        Assert.NotEqual(managed.Id, router.Id);
        Assert.Equal(TargetScope.Verdict, router.Scope);
        Assert.False(router.SystemManaged);
        Assert.Equal("Router", targets.Single(t => t.Address == "8.8.8.8" && t.Scope == TargetScope.Verdict).Parent);
    }
}
