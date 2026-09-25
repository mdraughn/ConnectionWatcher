using ConPersistence.Config;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>Every path that writes a target row stores the same normalized
/// values, and the import validator sees the row as it will be stored.</summary>
public class TargetWriteTests : IAsyncLifetime, IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;
    private readonly ConfigService _service;

    public TargetWriteTests()
    {
        _store = new ConfigStore(_factory);
        _service = new ConfigService(_factory, _store);
    }

    public Task InitializeAsync() => _store.SeedAsync(null); // built-in profiles
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private static TargetDto Padded(string name, string address) => new()
    {
        Name = name, Address = address, Profile = "Internal",
        Operator = "  Comcast ", Probe = " TCP:80 ",
    };

    private async Task<TargetRow> RowAsync(string name)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Targets.AsNoTracking().SingleAsync(t => t.Name == name);
    }

    private static void AssertNormalized(TargetRow row, string address)
    {
        Assert.Equal(address, row.Address);
        Assert.Equal("Comcast", row.Operator);
        Assert.Equal("tcp:80", row.Probe);
    }

    [Fact]
    public async Task Upsert_stores_the_normalized_row()
    {
        Assert.True((await _service.UpsertTargetAsync(Padded("ViaUpsert", " 10.0.0.9 "))).Success);
        AssertNormalized(await RowAsync("ViaUpsert"), "10.0.0.9");
    }

    [Fact]
    public async Task Insert_parent_stores_the_normalized_row()
    {
        var child = await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "Child", Address = "10.0.0.20", Profile = "Internal",
        });
        Assert.True(child.Success);

        var result = await _service.InsertParentAsync(Padded("ViaInsert", " 10.0.0.10 "), [child.Id!.Value]);

        Assert.True(result.Success);
        AssertNormalized(await RowAsync("ViaInsert"), "10.0.0.10");
        Assert.Equal(result.Id, (await RowAsync("Child")).ParentId);
    }

    [Fact]
    public async Task Import_stores_the_normalized_row_and_parents()
    {
        var result = await _service.ImportAsync(new ConfigDocument
        {
            Profiles = [new ProfileDto { Name = "Internal" }],
            Targets =
            [
                Padded("ViaImport", " 10.0.0.11 "),
                new TargetDto { Name = "Below", Address = "10.0.0.12", Profile = "Internal", Parent = "ViaImport" },
            ],
        });

        Assert.True(result.Success);
        var parent = await RowAsync("ViaImport");
        AssertNormalized(parent, "10.0.0.11");
        Assert.Equal(parent.Id, (await RowAsync("Below")).ParentId);
    }

    [Fact]
    public async Task Import_validates_addresses_the_way_they_will_be_stored()
    {
        // " 10.0.0.9 " is stored as "10.0.0.9": the validator must see the
        // duplicate, exactly as it does for the one-target CRUD path.
        var result = await _service.ImportAsync(new ConfigDocument
        {
            Profiles = [new ProfileDto { Name = "Internal" }],
            Targets =
            [
                new TargetDto { Name = "A", Address = "10.0.0.9", Profile = "Internal" },
                new TargetDto { Name = "B", Address = " 10.0.0.9 ", Profile = "Internal" },
            ],
        });

        Assert.False(result.Success);
        Assert.Contains(result.Errors!, e => e.Contains("Duplicate target address '10.0.0.9'"));
    }
}
