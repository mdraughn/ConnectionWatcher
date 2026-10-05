using ConCore.Discovery;
using ConEngine.Configuration;
using ConPersistence.Config;
using ConPersistence.Discoveries;

namespace ConPersistence.Tests;

public class DiscoveryAdoptTests : IAsyncLifetime, IDisposable
{
    private readonly TestDbFactory _factory = new();
    private ConfigService _service = null!;
    private DiscoveryStore _discoveries = null!;
    private ConfigStore _configStore = null!;

    public async Task InitializeAsync()
    {
        _configStore = new ConfigStore(_factory);
        _service = new ConfigService(_factory, _configStore);
        _discoveries = new DiscoveryStore(_factory);
        await _configStore.SeedAsync(new WatcherOptions
        {
            Targets = [new TargetConfig { Name = "Router", Address = "192.0.2.1", Profile = "Internal" }],
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private async Task<Guid> NewDiscoveryAsync(string address, DiscoveryKind kind = DiscoveryKind.LocalDevice)
    {
        await _discoveries.UpsertAsync([new(address, null, kind, DiscoveryMethod.ArpTable, "server", null)]);
        return (await _discoveries.ListAsync(null)).Single(d => d.Address == address).Id;
    }

    private static TargetDto Nas(string address = "192.0.2.50") => new()
    {
        Name = "NAS", Address = address, Profile = "Local", Scope = ConCore.Targets.TargetScope.Local,
    };

    [Fact]
    public async Task Adopting_creates_the_target_and_marks_the_discovery_in_one_commit()
    {
        var id = await NewDiscoveryAsync("192.0.2.50");

        var result = await _service.UpsertTargetAsync(Nas(), adoptDiscoveryId: id);

        Assert.True(result.Success);
        var row = Assert.Single(await _discoveries.ListAsync(DiscoveryStatus.Adopted));
        Assert.Equal(result.Id, row.AdoptedTargetId);
        Assert.Equal("NAS", row.AdoptedTargetName);
        Assert.Empty(await _discoveries.ListAsync(DiscoveryStatus.New));
    }

    [Fact]
    public async Task A_target_that_fails_validation_after_the_save_leaves_the_discovery_New()
    {
        var id = await NewDiscoveryAsync("192.0.2.1");   // same address as Router: the validator rejects it post-save

        var result = await _service.UpsertTargetAsync(Nas("192.0.2.1"), adoptDiscoveryId: id);

        Assert.False(result.Success);
        var row = Assert.Single(await _discoveries.ListAsync(null));
        Assert.Equal(DiscoveryStatus.New, row.Status);
        Assert.Null(row.AdoptedTargetId);
        Assert.Equal(["Router"], (await _configStore.LoadWatcherOptionsAsync()).Targets.Select(t => t.Name));
    }

    [Fact]
    public async Task A_duplicate_target_name_is_rejected_and_the_discovery_stays_New()
    {
        var id = await NewDiscoveryAsync("192.0.2.60");

        var result = await _service.UpsertTargetAsync(Nas("192.0.2.60") with { Name = "Router" }, adoptDiscoveryId: id);

        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        var row = Assert.Single(await _discoveries.ListAsync(null));
        Assert.Equal(DiscoveryStatus.New, row.Status);
        Assert.Null(row.AdoptedTargetId);
        Assert.Equal(["Router"], (await _configStore.LoadWatcherOptionsAsync()).Targets.Select(t => t.Name));
    }

    [Fact]
    public async Task A_live_adoption_cannot_be_adopted_again()
    {
        var id = await NewDiscoveryAsync("192.0.2.50");
        Assert.True((await _service.UpsertTargetAsync(Nas(), adoptDiscoveryId: id)).Success);

        var second = await _service.UpsertTargetAsync(Nas("192.0.2.51") with { Name = "NAS2" }, adoptDiscoveryId: id);

        Assert.False(second.Success);
        Assert.Equal(ConfigErrorKind.Conflict, second.Kind);
    }

    [Fact]
    public async Task After_the_target_is_deleted_the_discovery_can_be_adopted_again()
    {
        var id = await NewDiscoveryAsync("192.0.2.50");
        var first = await _service.UpsertTargetAsync(Nas(), adoptDiscoveryId: id);
        Assert.True((await _service.DeleteTargetAsync(first.Id!.Value)).Success);

        var again = await _service.UpsertTargetAsync(Nas(), adoptDiscoveryId: id);

        Assert.True(again.Success);
        var row = Assert.Single(await _discoveries.ListAsync(DiscoveryStatus.Adopted));
        Assert.Equal(again.Id, row.AdoptedTargetId);
    }

    [Fact]
    public async Task A_dismissed_discovery_can_be_adopted()
    {
        var id = await NewDiscoveryAsync("192.0.2.50");
        await _discoveries.DismissAsync(id);

        Assert.True((await _service.UpsertTargetAsync(Nas(), adoptDiscoveryId: id)).Success);
        Assert.Single(await _discoveries.ListAsync(DiscoveryStatus.Adopted));
    }

    [Fact]
    public async Task An_unknown_discovery_id_is_NotFound_and_creates_nothing()
    {
        var result = await _service.UpsertTargetAsync(Nas(), adoptDiscoveryId: Guid.NewGuid());

        Assert.Equal(ConfigErrorKind.NotFound, result.Kind);
        Assert.DoesNotContain((await _configStore.LoadWatcherOptionsAsync()).Targets, t => t.Name == "NAS");
    }

    [Fact]
    public async Task Adopting_while_editing_an_existing_target_is_rejected()
    {
        var id = await NewDiscoveryAsync("192.0.2.50");
        var routerId = (await _configStore.LoadWatcherOptionsAsync()).Targets.Single().Id!.Value;

        var result = await _service.UpsertTargetAsync(
            new TargetDto { Id = routerId, Name = "Router", Address = "192.0.2.1", Profile = "Internal" },
            adoptDiscoveryId: id);

        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        Assert.Equal(DiscoveryStatus.New, (await _discoveries.ListAsync(null)).Single().Status);
    }
}
