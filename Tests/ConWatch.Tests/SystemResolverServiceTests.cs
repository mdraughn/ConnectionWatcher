using System.Net;
using System.Net.Http.Json;
using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Settings;
using ConWatch.NameResolution;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>Spec 2026-10-10 §4.3, D7: the host keeps one Resolver target per OS resolver.</summary>
public class SystemResolverServiceTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public SystemResolverServiceTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private SystemResolverService Service => _factory.Services.GetRequiredService<SystemResolverService>();

    private async Task<List<TargetDto>> TargetsAsync() =>
        (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;

    private async Task<TargetDto?> ManagedAsync(string address) =>
        (await TargetsAsync()).SingleOrDefault(t => t.SystemManaged && t.Address == address);

    private async Task PutSettingAsync(string key, string value) =>
        (await _client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?> { [key] = value }))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task The_OS_resolver_becomes_a_managed_target_that_the_admin_can_only_disable()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");

        // The background loop may get there first; assert the state, not who reconciled.
        await Service.ReconcileNowAsync(CancellationToken.None);
        await ConfigApiTests.Eventually(async () => await ManagedAsync("10.0.0.53") is not null);
        var managed = await ManagedAsync("10.0.0.53");
        Assert.NotNull(managed);
        Assert.Equal("Your resolver 10.0.0.53", managed.Name);
        Assert.Equal("Resolver", managed.Profile);
        Assert.Equal("dns", managed.Probe);
        Assert.Equal(TargetScope.Resolver, managed.Scope);
        Assert.True(managed.Enabled);

        // Steady state: no change, no second target.
        Assert.False(await Service.ReconcileNowAsync(CancellationToken.None));
        Assert.Single(await TargetsAsync(), t => t.SystemManaged);

        // The engine probes it with the dns spec and reports it in status.
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/status");
            return status.GetProperty("targets").EnumerateArray()
                .Any(t => t.GetProperty("systemManaged").GetBoolean() && t.GetProperty("probe").GetString() == "dns");
        });
        Assert.Contains(_factory.Pings.Created, c => c.Ip.Equals(IPAddress.Parse("10.0.0.53")) && c.Probe.Kind == ConCore.ProbeKind.Dns);

        // Delete is refused; re-addressing is refused; disabling works.
        var delete = await _client.DeleteAsync($"/api/v1/targets/{managed.Id}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        var move = await _client.PutAsJsonAsync($"/api/v1/targets/{managed.Id}?addressChange=correction", managed with { Address = "10.0.0.54" });
        Assert.Equal(HttpStatusCode.BadRequest, move.StatusCode);
        (await _client.PutAsJsonAsync($"/api/v1/targets/{managed.Id}", managed with { Enabled = false })).EnsureSuccessStatusCode();
        Assert.False((await ManagedAsync("10.0.0.53"))!.Enabled);

        // The admin's disable sticks across reconciles.
        await Service.ReconcileNowAsync(CancellationToken.None);
        Assert.False((await ManagedAsync("10.0.0.53"))!.Enabled);
    }

    [Fact]
    public async Task A_vanished_resolver_is_retired_and_restored_when_it_returns()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53", "10.0.0.54");
        await Service.ReconcileNowAsync(CancellationToken.None);

        _factory.Resolvers.Set("10.0.0.53");
        await Service.ReconcileNowAsync(CancellationToken.None);
        await ConfigApiTests.Eventually(async () => (await ManagedAsync("10.0.0.54"))?.Enabled == false);
        Assert.True((await ManagedAsync("10.0.0.53"))!.Enabled);
        var settings = _factory.Services.GetRequiredService<SettingsStore>();
        // Ordering guarantee: the retired set is written before the config commit
        // (as a superset), so once the target is disabled its address is in the set.
        Assert.Contains("10.0.0.54", await settings.GetAsync(SettingKeys.DnsRetiredResolvers) ?? "");

        _factory.Resolvers.Set("10.0.0.53", "10.0.0.54");
        await Service.ReconcileNowAsync(CancellationToken.None);
        await ConfigApiTests.Eventually(async () => (await ManagedAsync("10.0.0.54"))?.Enabled == true);
        await ConfigApiTests.Eventually(async () =>
            !(await settings.GetAsync(SettingKeys.DnsRetiredResolvers) ?? "").Contains("10.0.0.54"));
        Assert.DoesNotContain("10.0.0.54", await settings.GetAsync(SettingKeys.DnsRetiredResolvers) ?? "");
    }

    [Fact]
    public async Task Turning_the_watch_off_retires_the_managed_targets_and_on_restores_them()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");
        await Service.ReconcileNowAsync(CancellationToken.None);

        await PutSettingAsync(SettingKeys.DnsEnabled, "false");
        await ConfigApiTests.Eventually(async () => (await ManagedAsync("10.0.0.53"))?.Enabled == false);

        await PutSettingAsync(SettingKeys.DnsEnabled, "true");
        await ConfigApiTests.Eventually(async () => (await ManagedAsync("10.0.0.53"))?.Enabled == true);
    }

    [Fact]
    public async Task Nothing_is_created_when_the_OS_list_is_empty()
    {
        // The factory's default source is empty: the home seed stays exactly what the test wrote.
        await ConfigApiTests.SeedHomeAsync(_client);
        Assert.False(await Service.ReconcileNowAsync(CancellationToken.None));
        Assert.DoesNotContain(await TargetsAsync(), t => t.SystemManaged);
    }
}
