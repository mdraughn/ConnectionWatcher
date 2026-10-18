using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence.Config;
using ConPersistence.Recording;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>
/// Every config or settings write rebuilds the engine. The outgoing engine's
/// determined states ride into the new one (WarmStartSeeder.FromEngine), so an
/// edit does not re-measure the world: no Starting parade, no new transitions.
/// A re-addressed target alone cold-starts; with the gap setting at 0 nothing
/// carries over.
/// </summary>
public sealed class RebuildCarryOverTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public RebuildCarryOverTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private WatcherHostService Host => _factory.Services.GetRequiredService<WatcherHostService>();

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private async Task<List<JsonElement>> TransitionRows()
    {
        await _factory.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
        return (await GetJson("/api/v1/transitions?limit=1000"))
            .GetProperty("items").EnumerateArray().ToList();
    }

    /// <summary>
    /// Target rows that left Starting, per target id. A cold-started monitor does
    /// not log entering Starting; the only record of it is the Starting-to-X
    /// exit. The HistoryReset marker an address change writes (Starting-to-Starting)
    /// is the config service's doing, not the engine's, so it is left out.
    /// </summary>
    private async Task<Dictionary<string, int>> StartingRowsPerTarget()
    {
        var result = new Dictionary<string, int>();
        foreach (var row in await TransitionRows())
        {
            if (row.GetProperty("subjectKind").GetString() != "Target" ||
                row.GetProperty("from").GetString() != "Starting" ||
                row.GetProperty("trigger").GetString() == "HistoryReset")
                continue;
            var id = row.GetProperty("subjectId").GetString()!;
            result[id] = result.GetValueOrDefault(id) + 1;
        }
        return result;
    }

    private async Task<JsonElement?> TargetStatus(string name)
    {
        var response = await _client.GetAsync("/api/v1/status");
        if (!response.IsSuccessStatusCode)
            return null;
        var status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        foreach (var t in status.GetProperty("targets").EnumerateArray())
            if (t.GetProperty("name").GetString() == name)
                return t;
        return null;
    }

    private async Task<TargetDto> TargetNamed(string name) =>
        (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!.Single(t => t.Name == name);

    /// <summary>Seeds the home config and waits until nothing is left to transition.</summary>
    private async Task<int> SeedAndSettleAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await HostTestWait.Eventually(() => HostTestWait.SteadyStateAsync(_client));
        return await HostTestWait.SettledRebuildCountAsync(Host);
    }

    /// <summary>Waits for the rebuild a write triggered to land, settle, and the new engine to answer.</summary>
    private async Task SettleAfterAsync(int rebuildsBefore)
    {
        await HostTestWait.Eventually(() => Task.FromResult(Host.RebuildCount > rebuildsBefore));
        await HostTestWait.SettledRebuildCountAsync(Host);
        await HostTestWait.Eventually(() => HostTestWait.SteadyStateAsync(_client));
    }

    [Fact]
    public async Task A_rename_rebuild_adds_no_transitions_and_keeps_states()
    {
        var rebuilds = await SeedAndSettleAsync();
        await TransitionRows(); // flushes the recorder

        // Empty the log, so a warm start from the database (the fallback seed) is
        // impossible: only the outgoing engine itself can carry the states over.
        await using (var db = await _factory.Services
            .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ConPersistence.ConWatchDbContext>>()
            .CreateDbContextAsync())
        {
            await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync(db.Transitions);
        }
        var countBefore = (await TransitionRows()).Count;
        Assert.Equal(0, countBefore);

        var cloudflare = await TargetNamed("Cloudflare");
        var put = await _client.PutAsJsonAsync($"/api/v1/targets/{cloudflare.Id}",
            cloudflare with { Name = "Cloudflare DNS" });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, put.StatusCode);

        // The first answer from the new engine already has Google Responding.
        await HostTestWait.Eventually(() => Task.FromResult(Host.RebuildCount > rebuilds));
        JsonElement? google = null;
        await HostTestWait.Eventually(async () => (google = await TargetStatus("Google")) is not null);
        Assert.Equal("Responding", google!.Value.GetProperty("state").GetString());

        await SettleAfterAsync(rebuilds);
        Assert.NotNull(await TargetStatus("Cloudflare DNS"));

        // Absence window: only after the rebuild settled and the new engine is steady.
        await Task.Delay(2000);
        Assert.Equal(countBefore, (await TransitionRows()).Count);
        var status = await GetJson("/api/v1/status");
        Assert.Equal("Healthy", status.GetProperty("verdict").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_re_addressed_target_alone_cold_starts()
    {
        var rebuilds = await SeedAndSettleAsync();
        var before = await StartingRowsPerTarget();
        var google = await TargetNamed("Google");

        var put = await _client.PutAsJsonAsync($"/api/v1/targets/{google.Id}?addressChange=correction",
            google with { Address = "198.51.100.8" });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, put.StatusCode);
        await SettleAfterAsync(rebuilds);

        // The cold-started Google re-proves itself out of Starting; wait for its row.
        var googleId = google.Id!.Value.ToString();
        await HostTestWait.Eventually(async () =>
            (await StartingRowsPerTarget()).GetValueOrDefault(googleId) > before.GetValueOrDefault(googleId));

        var after = await StartingRowsPerTarget();
        foreach (var (id, count) in after.Where(kv => kv.Key != googleId))
            Assert.Equal(before.GetValueOrDefault(id), count);
        Assert.Equal(before.Keys.Where(k => k != googleId).Order(), after.Keys.Where(k => k != googleId).Order());
    }

    [Fact]
    public async Task Carry_over_is_off_when_the_gap_setting_is_zero()
    {
        await SeedAndSettleAsync();

        // AUDIT: PUT /api/v1/settings rejects Engine.WarmRestartMaxGapMinutes as an
        // unknown setting (400), so the only way to set it is the store itself.
        // Is the key meant to be unreachable through the API?
        var settings = _factory.Services.GetRequiredService<ConPersistence.Settings.SettingsStore>();
        var rebuilds = Host.RebuildCount;
        await settings.SetManyAsync(new Dictionary<string, string?>
        {
            [ConPersistence.Settings.SettingKeys.WarmRestartMaxGapMinutes] = "0",
        });
        await SettleAfterAsync(rebuilds);
        rebuilds = await HostTestWait.SettledRebuildCountAsync(Host);
        var before = await StartingRowsPerTarget();

        var cloudflare = await TargetNamed("Cloudflare");
        var put = await _client.PutAsJsonAsync($"/api/v1/targets/{cloudflare.Id}",
            cloudflare with { Name = "Cloudflare DNS" });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, put.StatusCode);
        await SettleAfterAsync(rebuilds);

        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        Assert.Equal(3, targets!.Count);
        await HostTestWait.Eventually(async () =>
        {
            var after = await StartingRowsPerTarget();
            return targets.All(t => after.GetValueOrDefault(t.Id!.Value.ToString())
                > before.GetValueOrDefault(t.Id!.Value.ToString()));
        });
    }
}
