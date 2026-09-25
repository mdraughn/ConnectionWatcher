using System.Text.Json;
using ConEngine.Configuration;
using ConPersistence.Config;
using ConPersistence.Entities;

namespace ConPersistence.Tests;

public class ProfileMappingTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;
    private readonly ConfigService _service;

    public ProfileMappingTests()
    {
        _store = new ConfigStore(_factory);
        _service = new ConfigService(_factory, _store);
    }

    public void Dispose() => _factory.Dispose();

    private static string Json(PingProfileConfig config) => JsonSerializer.Serialize(config);

    private static PingProfileConfig ConfigOf(ProfileRow r) => new()
    {
        NormalIntervalSeconds = r.NormalIntervalSeconds, PingTimeoutSeconds = r.PingTimeoutSeconds,
        BurstIntervalSeconds = r.BurstIntervalSeconds, BurstMaxDurationSeconds = r.BurstMaxDurationSeconds,
        HistorySize = r.HistorySize, MinUsableSample = r.MinUsableSample,
        RespondingThreshold = r.RespondingThreshold, FalteringThreshold = r.FalteringThreshold,
        DownThreshold = r.DownThreshold, RecoveringThreshold = r.RecoveringThreshold,
    };

    [Fact]
    public async Task A_profile_posted_with_only_a_name_gets_the_config_defaults()
    {
        Assert.True((await _service.UpsertProfileAsync(new ProfileDto { Name = "Bare" })).Success);

        var options = await _store.LoadWatcherOptionsAsync();

        Assert.Equal(Json(new PingProfileConfig()), Json(options.Profiles["Bare"]));
    }

    [Fact]
    public void A_new_row_starts_from_the_config_defaults() =>
        Assert.Equal(Json(new PingProfileConfig()), Json(ConfigOf(new ProfileRow())));

    [Fact]
    public async Task Every_field_survives_dto_to_row_to_dto_and_config()
    {
        var dto = new ProfileDto
        {
            Name = "Tuned", NormalIntervalSeconds = 7, PingTimeoutSeconds = 2,
            BurstIntervalSeconds = 0.5, BurstMaxDurationSeconds = 30, HistorySize = 50,
            MinUsableSample = 8, RespondingThreshold = 0.9, FalteringThreshold = 0.7,
            DownThreshold = 0.05, RecoveringThreshold = 0.3,
        };
        Assert.True((await _service.UpsertProfileAsync(dto)).Success);

        var exported = (await _store.ExportAsync()).Profiles.Single(p => p.Name == "Tuned");
        Assert.Equal(dto with { Id = exported.Id }, exported);

        var config = (await _store.LoadWatcherOptionsAsync()).Profiles["Tuned"];
        Assert.Equal(Json(new PingProfileConfig
        {
            NormalIntervalSeconds = 7, PingTimeoutSeconds = 2, BurstIntervalSeconds = 0.5,
            BurstMaxDurationSeconds = 30, HistorySize = 50, MinUsableSample = 8,
            RespondingThreshold = 0.9, FalteringThreshold = 0.7, DownThreshold = 0.05,
            RecoveringThreshold = 0.3,
        }), Json(config));
    }
}
