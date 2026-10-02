using ConPersistence.Config;

namespace ConPersistence.Tests;

public class ConfigServiceTests : IAsyncLifetime, IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;
    private readonly ConfigService _service;
    private int _changeSignals;

    public ConfigServiceTests()
    {
        _store = new ConfigStore(_factory);
        _service = new ConfigService(_factory, _store);
        _service.ConfigChanged += () => _changeSignals++;
    }

    public async Task InitializeAsync() => await _store.SeedAsync(ConfigStoreTests.ValidHome());
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private async Task<Guid> IdOf(string targetName) =>
        (await _store.LoadWatcherOptionsAsync()).Targets.Single(t => t.Name == targetName).Id!.Value;

    [Fact]
    public async Task Create_target_succeeds_and_signals()
    {
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "TV", Address = "192.168.1.50", Parent = "Router", Profile = "Internal",
        });
        Assert.True(result.Success);
        Assert.NotNull(result.Id);
        Assert.Equal(1, _changeSignals);
        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Contains(options.Targets, t => t.Name == "TV" && t.Parent == "Router");
    }

    [Fact]
    public async Task Reparenting_into_cycle_is_rejected_and_rolled_back()
    {
        var routerId = await IdOf("Router");
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Id = routerId, Name = "Router", Address = "192.168.1.1",
            Parent = "Google", Profile = "Internal",
        });
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        Assert.Contains(result.Errors!, e => e.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, _changeSignals);
        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Null(options.Targets.Single(t => t.Name == "Router").Parent);
    }

    [Fact]
    public async Task Unknown_parent_name_is_validation_error()
    {
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "TV", Address = "192.168.1.50", Parent = "Ghost",
        });
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
    }

    [Fact]
    public async Task Delete_target_with_children_conflicts()
    {
        var result = await _service.DeleteTargetAsync(await IdOf("Modem"));
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Conflict, result.Kind);
    }

    [Fact]
    public async Task Delete_pool_member_target_conflicts()
    {
        var result = await _service.DeleteTargetAsync(await IdOf("Google"));
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Conflict, result.Kind);
    }

    [Fact]
    public async Task Delete_profile_in_use_conflicts()
    {
        var doc = await _store.ExportAsync();
        var internalProfile = doc.Profiles.Single(p => p.Name == "Internal");
        var result = await _service.DeleteProfileAsync(internalProfile.Id!.Value);
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Conflict, result.Kind);
    }

    [Fact]
    public async Task Delete_leaf_target_succeeds_after_pool_removal()
    {
        var update = await _service.UpsertPoolAsync(new PoolDto
        {
            Id = (await _store.ExportAsync()).Pools.Single().Id,
            Name = "Internet", Members = ["Cloudflare"], Parent = "Modem",
        });
        Assert.True(update.Success);
        var result = await _service.DeleteTargetAsync(await IdOf("Google"));
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Delete_unknown_id_is_not_found()
    {
        var result = await _service.DeleteTargetAsync(Guid.NewGuid());
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task Address_edit_without_kind_is_rejected()
    {
        var routerId = await IdOf("Router");
        var result = await _service.UpsertTargetAsync(new ConPersistence.Config.TargetDto
        {
            Id = routerId, Name = "Router", Address = "192.168.1.254", Profile = "Internal",
        });
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        Assert.Contains(result.Errors!, e => e.Contains("addressChange"));
        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Equal("192.168.1.1", options.Targets.Single(t => t.Name == "Router").Address);
    }

    [Theory]
    [InlineData("correction", "HistoryReset")]
    [InlineData("repoint", "HistoryReset")]
    [InlineData("renumber", "AddressChanged")]
    public async Task Address_edit_records_the_right_marker(string kind, string expectedTrigger)
    {
        var routerId = await IdOf("Router");
        var result = await _service.UpsertTargetAsync(new ConPersistence.Config.TargetDto
        {
            Id = routerId, Name = "Router", Address = "192.168.1.254", Profile = "Internal",
        }, addressChange: kind);
        Assert.True(result.Success);

        await using var db = _factory.CreateDbContext();
        var marker = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(db.Transitions)).Single();
        Assert.Equal(expectedTrigger, marker.Trigger);
        Assert.Equal(routerId, marker.SubjectId);
        Assert.Equal("Target", marker.SubjectKind);
        Assert.Contains("192.168.1.1", marker.Detail);
        Assert.Contains("192.168.1.254", marker.Detail);
        Assert.Contains(kind, marker.Detail);
    }

    [Fact]
    public async Task Non_address_edit_needs_no_kind_and_records_no_marker()
    {
        var routerId = await IdOf("Router");
        var result = await _service.UpsertTargetAsync(new ConPersistence.Config.TargetDto
        {
            Id = routerId, Name = "Router Renamed", Address = "192.168.1.1", Profile = "Internal",
        });
        Assert.True(result.Success);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .CountAsync(db.Transitions));
    }

    [Fact]
    public async Task Invalid_address_change_kind_is_rejected()
    {
        var routerId = await IdOf("Router");
        var result = await _service.UpsertTargetAsync(new ConPersistence.Config.TargetDto
        {
            Id = routerId, Name = "Router", Address = "192.168.1.254", Profile = "Internal",
        }, addressChange: "oops");
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
    }

    [Fact]
    public async Task InsertParent_slides_a_node_above_shared_children()
    {
        var options = await _store.LoadWatcherOptionsAsync();
        var google = options.Targets.Single(t => t.Name == "Google").Id!.Value;
        var cloudflare = options.Targets.Single(t => t.Name == "Cloudflare").Id!.Value;

        var result = await _service.InsertParentAsync(new ConPersistence.Config.TargetDto
        {
            Name = "IspHop", Address = "198.51.100.11", Profile = "Public",
        }, [google, cloudflare]);
        Assert.True(result.Success);
        Assert.Equal(1, _changeSignals);

        var after = await _store.LoadWatcherOptionsAsync();
        Assert.Equal("Modem", after.Targets.Single(t => t.Name == "IspHop").Parent);
        Assert.Equal("IspHop", after.Targets.Single(t => t.Name == "Google").Parent);
        Assert.Equal("IspHop", after.Targets.Single(t => t.Name == "Cloudflare").Parent);
    }

    [Fact]
    public async Task InsertParent_rejects_children_with_mixed_parents()
    {
        var options = await _store.LoadWatcherOptionsAsync();
        var router = options.Targets.Single(t => t.Name == "Router").Id!.Value;   // parent: none
        var google = options.Targets.Single(t => t.Name == "Google").Id!.Value;   // parent: Modem

        var result = await _service.InsertParentAsync(new ConPersistence.Config.TargetDto
        {
            Name = "X", Address = "10.9.9.9",
        }, [router, google]);
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        Assert.Equal(0, _changeSignals);
    }

    [Fact]
    public async Task InsertParent_rejects_unknown_child()
    {
        var result = await _service.InsertParentAsync(new ConPersistence.Config.TargetDto
        {
            Name = "X", Address = "10.9.9.9",
        }, [Guid.NewGuid()]);
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
    }

    [Fact]
    public async Task InsertParent_rejects_empty_children()
    {
        var result = await _service.InsertParentAsync(new ConPersistence.Config.TargetDto
        {
            Name = "X", Address = "10.9.9.9",
        }, []);
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
    }

    [Fact]
    public async Task Import_roundtrips_into_fresh_db()
    {
        var doc = await _store.ExportAsync();

        using var freshFactory = new TestDbFactory();
        var freshStore = new ConfigStore(freshFactory);
        var freshService = new ConfigService(freshFactory, freshStore);
        var result = await freshService.ImportAsync(doc);
        Assert.True(result.Success);

        var reExport = await freshStore.ExportAsync();
        Assert.Equal(doc.Targets.OrderBy(t => t.Name), reExport.Targets.OrderBy(t => t.Name));
        Assert.Equal(doc.Pools.Single().Members, reExport.Pools.Single().Members);
    }

    [Fact]
    public async Task Import_of_an_old_export_naming_External_restores_the_builtins()
    {
        var doc = new ConfigDocument
        {
            Profiles =
            [
                new ProfileDto { Name = "Internal" },
                new ProfileDto { Name = "External", NormalIntervalSeconds = 10 },
            ],
            Targets = [new TargetDto { Name = "Google", Address = "8.8.8.8", Profile = "External" }],
        };

        var result = await _service.ImportAsync(doc);
        Assert.True(result.Success);

        var export = await _store.ExportAsync();
        Assert.Equal(["External", "Internal", "Local", "Path", "Public"], export.Profiles.Select(p => p.Name).Order().ToArray());
        Assert.Equal("External", export.Targets.Single().Profile);
        Assert.All(export.Profiles.Where(p => p.Name is "Path" or "Public"), p => Assert.NotNull(p.Description));
    }

    [Fact]
    public async Task Import_invalid_document_changes_nothing()
    {
        var doc = await _store.ExportAsync();
        doc.Targets.Add(new TargetDto { Name = "Bad", Address = "1.2.3.4", Parent = "Nope" });
        var result = await _service.ImportAsync(doc);
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        var options = await _store.LoadWatcherOptionsAsync();
        Assert.DoesNotContain(options.Targets, t => t.Name == "Bad");
        Assert.Equal(4, options.Targets.Count);
    }

    [Fact]
    public async Task EditDocument_commits_once_and_keeps_untouched_ids()
    {
        var routerId = await IdOf("Router");

        var result = await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto { Name = "TV", Address = "192.168.1.50", Parent = "Router", Profile = "Internal" });
            return ConfigResult.Ok();
        });

        Assert.True(result.Success);
        Assert.Equal(1, _changeSignals);
        Assert.Equal(routerId, await IdOf("Router"));
        Assert.Contains((await _store.LoadWatcherOptionsAsync()).Targets, t => t.Name == "TV" && t.Parent == "Router");
    }

    [Fact]
    public async Task EditDocument_that_fails_validation_commits_nothing()
    {
        var before = (await _store.LoadWatcherOptionsAsync()).Targets.Count;

        var result = await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto { Name = "Good", Address = "192.168.1.60", Profile = "Internal" });
            doc.Targets.Add(new TargetDto { Name = "", Address = "", Profile = "Internal" });
            return ConfigResult.Ok();
        });

        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        Assert.Equal(0, _changeSignals);
        Assert.Equal(before, (await _store.LoadWatcherOptionsAsync()).Targets.Count);
    }

    [Fact]
    public async Task EditDocument_changing_an_existing_targets_address_is_rejected()
    {
        var routerId = await IdOf("Router");
        var originalAddress = (await _store.LoadWatcherOptionsAsync())
            .Targets.Single(t => t.Name == "Router").Address;

        var result = await _service.EditDocumentAsync(doc =>
        {
            var i = doc.Targets.FindIndex(t => t.Id == routerId);
            doc.Targets[i] = doc.Targets[i] with { Address = "10.10.10.10" };
            return ConfigResult.Ok();
        });

        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        Assert.Contains(result.Errors!, e => e.Contains("EditDocumentAsync cannot change"));
        Assert.Equal(0, _changeSignals);
        Assert.Equal(originalAddress, (await _store.LoadWatcherOptionsAsync())
            .Targets.Single(t => t.Name == "Router").Address);
    }

    [Fact]
    public async Task EditDocument_adding_a_target_without_renaming_others_still_succeeds()
    {
        var result = await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto { Name = "NewGuy", Address = "192.168.1.70", Parent = "Router", Profile = "Internal" });
            return ConfigResult.Ok();
        });

        Assert.True(result.Success);
        Assert.Equal(1, _changeSignals);
        Assert.Contains((await _store.LoadWatcherOptionsAsync()).Targets, t => t.Name == "NewGuy" && t.Parent == "Router");
    }

    [Fact]
    public async Task Profile_description_is_trimmed_and_round_trips()
    {
        var result = await _service.UpsertProfileAsync(new ConPersistence.Config.ProfileDto
        {
            Name = "Described", Description = "  Things I own.  ",
        });
        Assert.True(result.Success);
        var doc = await _store.ExportAsync();
        Assert.Equal("Things I own.", doc.Profiles.Single(p => p.Name == "Described").Description);
    }

    [Fact]
    public async Task Blank_profile_description_is_stored_as_null()
    {
        var result = await _service.UpsertProfileAsync(new ConPersistence.Config.ProfileDto
        {
            Name = "Blank", Description = "   ",
        });
        Assert.True(result.Success);
        Assert.Null((await _store.ExportAsync()).Profiles.Single(p => p.Name == "Blank").Description);
    }

    [Fact]
    public async Task Overlong_profile_description_is_rejected()
    {
        var result = await _service.UpsertProfileAsync(new ConPersistence.Config.ProfileDto
        {
            Name = "Long", Description = new string('x', 501),
        });
        Assert.False(result.Success);
        Assert.Equal(ConPersistence.Config.ConfigErrorKind.Validation, result.Kind);
        Assert.Contains(result.Errors!, e => e.Contains("500"));
    }

    [Fact]
    public async Task Import_rejects_an_overlong_description()
    {
        var doc = await _store.ExportAsync();
        doc.Profiles.Add(new ConPersistence.Config.ProfileDto { Name = "Long", Description = new string('x', 501) });
        var result = await _service.ImportAsync(doc);
        Assert.False(result.Success);
        Assert.Contains(result.Errors!, e => e.Contains("Long") && e.Contains("500"));
    }

    [Fact]
    public async Task Creating_a_Local_root_target_succeeds()
    {
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = ConCore.Targets.TargetScope.Local,
        });
        Assert.True(result.Success);
        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Equal(ConCore.Targets.TargetScope.Local, options.Targets.Single(t => t.Name == "NAS").Scope);
    }

    [Fact]
    public async Task Switching_a_target_that_has_children_to_Local_is_rejected_and_rolled_back()
    {
        var routerId = await IdOf("Router"); // parent of Modem
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Id = routerId, Name = "Router", Address = "192.168.1.1", Profile = "Internal",
            Scope = ConCore.Targets.TargetScope.Local,
        });
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        Assert.Contains(result.Errors!, e => e.Contains("Local targets cannot have children"));
        Assert.Equal(0, _changeSignals);
        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Equal(ConCore.Targets.TargetScope.Verdict, options.Targets.Single(t => t.Name == "Router").Scope);
    }

    [Fact]
    public async Task Switching_a_pool_member_to_Local_is_rejected()
    {
        var googleId = await IdOf("Google"); // member of Internet
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Id = googleId, Name = "Google", Address = "8.8.8.8", Parent = "Modem", Profile = "Public",
            Scope = ConCore.Targets.TargetScope.Local,
        });
        Assert.False(result.Success);
        Assert.Contains(result.Errors!, e => e.Contains("Local targets cannot be pool members")
            || e.Contains("cannot have a parent"));
    }

    [Fact]
    public async Task A_target_cannot_be_parented_to_a_Local_target()
    {
        Assert.True((await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = ConCore.Targets.TargetScope.Local,
        })).Success);
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "Backup", Address = "203.0.113.10", Parent = "NAS", Profile = "Internal",
        });
        Assert.False(result.Success);
        Assert.Contains(result.Errors!, e => e.Contains("Local targets cannot have children"));
    }
}
