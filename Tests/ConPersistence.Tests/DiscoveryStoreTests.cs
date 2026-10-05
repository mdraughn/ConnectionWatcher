using ConCore.Discovery;
using ConEngine.Configuration;
using ConPersistence.Config;
using ConPersistence.Discoveries;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

public class DiscoveryStoreTests : IDisposable
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly TestDbFactory _factory = new();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly DiscoveryStore _store;

    public DiscoveryStoreTests() => _store = new DiscoveryStore(_factory, _clock);

    public void Dispose() => _factory.Dispose();

    private static DiscoveryInput Router(string address = "192.0.2.1", string evidence = "answers pings in 1 ms") =>
        new(address, null, DiscoveryKind.Gateway, DiscoveryMethod.DefaultRoute, "server", evidence);

    [Fact]
    public async Task A_new_discovery_is_stored_as_New_with_both_seen_times()
    {
        var result = await _store.UpsertAsync([Router()]);

        Assert.True(result.Success);
        Assert.Equal((1, 0), (result.Created, result.Updated));
        var row = Assert.Single(await _store.ListAsync(DiscoveryStatus.New));
        Assert.Equal("192.0.2.1", row.Address);
        Assert.Equal(DiscoveryKind.Gateway, row.Kind);
        Assert.Equal("server", row.Origin);
        Assert.Equal(_clock.Now.UtcDateTime, row.FirstSeenUtc);
        Assert.Equal(row.FirstSeenUtc, row.LastSeenUtc);
        Assert.Equal(DateTimeKind.Utc, row.LastSeenUtc.Kind);
    }

    [Fact]
    public async Task Rediscovery_updates_the_row_instead_of_duplicating_it()
    {
        await _store.UpsertAsync([Router(evidence: "old")]);
        _clock.Now = _clock.Now.AddHours(2);

        var result = await _store.UpsertAsync(
            [new("192.0.2.1", "gw.example.com", DiscoveryKind.Gateway, DiscoveryMethod.CommonAddress, "tray:PC", "new")]);

        Assert.Equal((0, 1), (result.Created, result.Updated));
        var row = Assert.Single(await _store.ListAsync(null));
        Assert.Equal("new", row.Evidence);
        Assert.Equal("gw.example.com", row.Hostname);
        Assert.Equal(DiscoveryMethod.CommonAddress, row.Method);
        Assert.Equal("tray:PC", row.Origin);
        Assert.Equal(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc), row.FirstSeenUtc);
        Assert.Equal(new DateTime(2026, 10, 3, 11, 0, 0, DateTimeKind.Utc), row.LastSeenUtc);
    }

    [Fact]
    public async Task The_same_address_with_a_different_kind_is_a_different_discovery()
    {
        await _store.UpsertAsync([Router(), new("192.0.2.1", null, DiscoveryKind.LocalDevice,
            DiscoveryMethod.ArpTable, "server", null)]);
        Assert.Equal(2, (await _store.ListAsync(null)).Count);
    }

    [Fact]
    public async Task Rediscovering_a_dismissed_address_keeps_it_dismissed_but_refreshes_it()
    {
        await _store.UpsertAsync([Router()]);
        var id = (await _store.ListAsync(null)).Single().Id;
        Assert.True((await _store.DismissAsync(id)).Success);
        _clock.Now = _clock.Now.AddDays(1);

        await _store.UpsertAsync([Router(evidence: "still there")]);

        Assert.Empty(await _store.ListAsync(DiscoveryStatus.New));
        var row = Assert.Single(await _store.ListAsync(DiscoveryStatus.Dismissed));
        Assert.Equal("still there", row.Evidence);
        Assert.Equal(new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc), row.LastSeenUtc);
    }

    [Fact]
    public async Task Duplicates_inside_one_batch_merge_last_one_wins()
    {
        var result = await _store.UpsertAsync([Router(evidence: "first"), Router(evidence: "second")]);
        Assert.Equal((1, 0), (result.Created, result.Updated));
        Assert.Equal("second", Assert.Single(await _store.ListAsync(null)).Evidence);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("router.example.com")]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    public async Task Unusable_addresses_are_rejected_and_nothing_is_written(string address)
    {
        var result = await _store.UpsertAsync([Router("192.0.2.9"), Router(address)]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.StartsWith("Item 2"));
        Assert.Empty(await _store.ListAsync(null)); // the valid first item was not written either
    }

    [Fact]
    public async Task Undefined_enum_values_missing_origin_and_oversized_text_are_rejected()
    {
        var errors = DiscoveryStore.Validate(
        [
            new("192.0.2.1", null, (DiscoveryKind)9, DiscoveryMethod.DefaultRoute, "server", null),
            new("192.0.2.2", null, DiscoveryKind.Gateway, (DiscoveryMethod)42, "server", null),
            new("192.0.2.3", null, DiscoveryKind.Gateway, DiscoveryMethod.DefaultRoute, " ", null),
            new("192.0.2.4", new string('h', 256), DiscoveryKind.Gateway, DiscoveryMethod.DefaultRoute, "server", null),
            new("192.0.2.5", null, DiscoveryKind.Gateway, DiscoveryMethod.DefaultRoute, "server", new string('e', 1001)),
        ]);
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public async Task An_empty_or_oversized_batch_is_rejected()
    {
        Assert.False((await _store.UpsertAsync([])).Success);
        var big = Enumerable.Range(1, DiscoveryStore.MaxBatch + 1)
            .Select(i => Router($"198.51.{i / 250}.{i % 250 + 1}")).ToList();
        Assert.False((await _store.UpsertAsync(big)).Success);
    }

    [Fact]
    public async Task List_filters_by_status_and_puts_the_most_recently_seen_first()
    {
        await _store.UpsertAsync([Router("192.0.2.1")]);
        _clock.Now = _clock.Now.AddMinutes(5);
        await _store.UpsertAsync([Router("192.0.2.2")]);
        var oldId = (await _store.ListAsync(null)).Single(d => d.Address == "192.0.2.1").Id;
        await _store.DismissAsync(oldId);

        Assert.Equal(["192.0.2.2"], (await _store.ListAsync(DiscoveryStatus.New)).Select(d => d.Address));
        Assert.Equal(["192.0.2.1"], (await _store.ListAsync(DiscoveryStatus.Dismissed)).Select(d => d.Address));
        Assert.Equal(["192.0.2.2", "192.0.2.1"], (await _store.ListAsync(null)).Select(d => d.Address));
    }

    [Fact]
    public async Task Dismiss_and_restore_move_between_New_and_Dismissed()
    {
        await _store.UpsertAsync([Router()]);
        var id = (await _store.ListAsync(null)).Single().Id;

        Assert.True((await _store.DismissAsync(id)).Success);
        Assert.True((await _store.DismissAsync(id)).Success);          // idempotent
        Assert.Equal(DiscoveryStatus.Dismissed, (await _store.ListAsync(null)).Single().Status);

        Assert.True((await _store.RestoreAsync(id)).Success);
        Assert.True((await _store.RestoreAsync(id)).Success);          // idempotent
        Assert.Equal(DiscoveryStatus.New, (await _store.ListAsync(null)).Single().Status);
    }

    [Fact]
    public async Task Unknown_ids_are_NotFound()
    {
        var dismiss = await _store.DismissAsync(Guid.NewGuid());
        var restore = await _store.RestoreAsync(Guid.NewGuid());
        Assert.Equal(ConfigErrorKind.NotFound, dismiss.Kind);
        Assert.Equal(ConfigErrorKind.NotFound, restore.Kind);
    }

    [Fact]
    public async Task Restore_all_dismissed_returns_how_many_came_back()
    {
        await _store.UpsertAsync([Router("192.0.2.1"), Router("192.0.2.2"), Router("192.0.2.3")]);
        foreach (var d in (await _store.ListAsync(null)).Take(2))
            await _store.DismissAsync(d.Id);

        Assert.Equal(2, await _store.RestoreAllDismissedAsync());
        Assert.Empty(await _store.ListAsync(DiscoveryStatus.Dismissed));
        Assert.Equal(3, (await _store.ListAsync(DiscoveryStatus.New)).Count);
        Assert.Equal(0, await _store.RestoreAllDismissedAsync());
    }

    private async Task<(Guid DiscoveryId, Guid TargetId)> AdoptedAsync(bool targetStillExists)
    {
        var configStore = new ConfigStore(_factory);
        await configStore.SeedAsync(new WatcherOptions
        {
            Targets = [new TargetConfig { Name = "Router", Address = "192.0.2.1", Profile = "Internal" }],
        });
        var targetId = (await configStore.LoadWatcherOptionsAsync()).Targets.Single().Id!.Value;
        await _store.UpsertAsync([Router()]);
        var id = (await _store.ListAsync(null)).Single().Id;
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Discoveries.SingleAsync(d => d.Id == id);
            row.Status = DiscoveryStatus.Adopted;
            row.AdoptedTargetId = targetId;
            if (!targetStillExists)
            {
                db.Targets.Remove(await db.Targets.SingleAsync(t => t.Id == targetId));
            }
            await db.SaveChangesAsync();
        }
        return (id, targetId);
    }

    [Fact]
    public async Task A_live_adoption_cannot_be_dismissed_or_restored_and_names_its_target()
    {
        var (id, _) = await AdoptedAsync(targetStillExists: true);

        Assert.Equal(ConfigErrorKind.Conflict, (await _store.DismissAsync(id)).Kind);
        Assert.Equal(ConfigErrorKind.Conflict, (await _store.RestoreAsync(id)).Kind);
        var row = Assert.Single(await _store.ListAsync(DiscoveryStatus.Adopted));
        Assert.Equal("Router", row.AdoptedTargetName);
    }

    [Fact]
    public async Task An_adoption_whose_target_was_deleted_shows_no_name_and_can_be_dismissed_or_restored()
    {
        var (id, _) = await AdoptedAsync(targetStillExists: false);

        var row = Assert.Single(await _store.ListAsync(DiscoveryStatus.Adopted));
        Assert.Null(row.AdoptedTargetName);                 // "(target removed)" in the UI
        Assert.NotNull(row.AdoptedTargetId);

        Assert.True((await _store.RestoreAsync(id)).Success);
        var back = Assert.Single(await _store.ListAsync(DiscoveryStatus.New));
        Assert.Null(back.AdoptedTargetId);
    }

    [Theory]
    [InlineData(null, DiscoveryStatus.New, true)]
    [InlineData("", DiscoveryStatus.New, true)]
    [InlineData("NEW", DiscoveryStatus.New, true)]
    [InlineData("dismissed", DiscoveryStatus.Dismissed, true)]
    [InlineData("Adopted", DiscoveryStatus.Adopted, true)]
    [InlineData("all", null, true)]
    [InlineData("bogus", null, false)]
    public void TryParseFilter_understands_the_four_words(string? text, DiscoveryStatus? expected, bool ok)
    {
        Assert.Equal(ok, DiscoveryStatuses.TryParseFilter(text, out var status));
        if (ok) Assert.Equal(expected, status);
    }
}
