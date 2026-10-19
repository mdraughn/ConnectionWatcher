using System.Net;
using System.Net.Http.Json;
using ConCore.Discovery;
using ConPersistence;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>Task 28 (HA3 = PS10, ruling D1): both erase operations delete every
/// measurement table, and every table is classified so a new one cannot be
/// forgotten.</summary>
public class AdminEraseTests : IDisposable
{
    private enum Kind { Config, Measurement, Settings, Inbox }

    // Literal map: an entity missing here fails the test, naming itself.
    private static readonly Dictionary<Type, Kind> Classification = new()
    {
        [typeof(ProfileRow)] = Kind.Config,
        [typeof(TargetRow)] = Kind.Config,
        [typeof(PoolRow)] = Kind.Config,
        [typeof(PoolMemberRow)] = Kind.Config,
        [typeof(TransitionRow)] = Kind.Measurement,
        [typeof(RollupRow)] = Kind.Measurement,
        [typeof(ModelWarningRow)] = Kind.Measurement,
        [typeof(GlitchRow)] = Kind.Measurement,
        [typeof(SettingRow)] = Kind.Settings,
        // Inbox: clear-history keeps it (it is not history); factory reset deletes it.
        [typeof(DiscoveryRow)] = Kind.Inbox,
    };

    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public AdminEraseTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<int> CountAsync(ConWatchDbContext db, Type entity) => entity.Name switch
    {
        nameof(ProfileRow) => await db.Profiles.CountAsync(),
        nameof(TargetRow) => await db.Targets.CountAsync(),
        nameof(PoolRow) => await db.Pools.CountAsync(),
        nameof(PoolMemberRow) => await db.PoolMembers.CountAsync(),
        nameof(TransitionRow) => await db.Transitions.CountAsync(),
        nameof(RollupRow) => await db.Rollups.CountAsync(),
        nameof(ModelWarningRow) => await db.ModelWarnings.CountAsync(),
        nameof(GlitchRow) => await db.Glitches.CountAsync(),
        nameof(SettingRow) => await db.Settings.CountAsync(),
        nameof(DiscoveryRow) => await db.Discoveries.CountAsync(),
        _ => throw new InvalidOperationException($"No counter for {entity.Name}"),
    };

    [Fact]
    public async Task Every_table_is_classified_and_erased_as_classified()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync();
        var entityTypes = db.Model.GetEntityTypes().Select(e => e.ClrType).ToList();
        foreach (var t in entityTypes)
            Assert.True(Classification.ContainsKey(t),
                $"Entity {t.Name} is not classified (config/measurement/settings/inbox) in AdminEraseTests.");

        // What a fresh start leaves: builtin profiles + seeded settings.
        var builtinProfiles = await db.Profiles.Select(p => p.Name).OrderBy(n => n).ToListAsync();
        // AUDIT: Retention.LastRunUtc is written by the retention job, not seeded; a factory
        // reset deletes it and nothing re-creates it until the job next runs. Looks intended
        // (the job simply runs again), pinned as is.
        var seededSettings = await db.Settings.Where(s => s.Key != "Retention.LastRunUtc")
            .Select(s => s.Key).OrderBy(k => k).ToListAsync();
        Assert.NotEmpty(builtinProfiles);
        Assert.NotEmpty(seededSettings);

        // One row per table (profiles and settings already have rows; add one of each).
        var profile = new ProfileRow { Id = Guid.NewGuid(), Name = "Erase-test profile" };
        var target = new TargetRow
        {
            Id = Guid.NewGuid(), Name = "erase-target", Address = "192.0.2.10", ProfileId = profile.Id,
        };
        var pool = new PoolRow { Id = Guid.NewGuid(), Name = "erase-pool" };
        db.Profiles.Add(profile);
        db.Targets.Add(target);
        db.Pools.Add(pool);
        db.PoolMembers.Add(new PoolMemberRow { PoolId = pool.Id, TargetId = target.Id, MemberIndex = 0 });
        db.Transitions.Add(new TransitionRow
            { TimestampUtc = now, SubjectId = target.Id, SubjectName = "erase-target", FromState = "Responding", ToState = "Down", Trigger = "t" });
        db.Rollups.Add(new RollupRow { TargetId = target.Id, PeriodStartUtc = now, PingCount = 1, SuccessCount = 1 });
        db.ModelWarnings.Add(new ModelWarningRow { Key = "erase-test", Kind = "UnprovableUp", Message = "m", StartUtc = now });
        db.Glitches.Add(new GlitchRow
            { TargetId = target.Id, TargetName = "erase-target", StartUtc = now, EndUtc = now, LostCount = 1, SampleCount = 10 });
        db.Settings.Add(new SettingRow { Key = "Erase.Test", Value = "x" });
        db.Discoveries.Add(new DiscoveryRow
        {
            Id = Guid.NewGuid(), Address = "192.0.2.50", Kind = DiscoveryKind.LocalDevice,
            Method = DiscoveryMethod.ArpTable, FirstSeenUtc = now, LastSeenUtc = now,
        });
        await db.SaveChangesAsync();

        foreach (var t in entityTypes)
            Assert.True(await CountAsync(db, t) > 0, $"Seed failed: {t.Name} has no row.");

        // Clear history: measurements empty; config, settings and inbox kept.
        var clear = await _client.PostAsJsonAsync("/api/v1/admin/clear-history", new { confirm = "CLEAR" });
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        await using (var after = await dbFactory.CreateDbContextAsync())
        {
            foreach (var (t, kind) in Classification)
            {
                var n = await CountAsync(after, t);
                if (kind == Kind.Measurement)
                    Assert.True(n == 0, $"Clear history left {n} {t.Name} rows.");
                else
                    Assert.True(n > 0, $"Clear history deleted {t.Name} ({kind}).");
            }
        }

        // Re-seed the measurements clear-history removed, so factory reset must delete them itself.
        await using (var again = await dbFactory.CreateDbContextAsync())
        {
            again.Transitions.Add(new TransitionRow
                { TimestampUtc = now, SubjectId = target.Id, SubjectName = "erase-target", FromState = "Responding", ToState = "Down", Trigger = "t" });
            again.Rollups.Add(new RollupRow { TargetId = target.Id, PeriodStartUtc = now, PingCount = 1, SuccessCount = 1 });
            again.ModelWarnings.Add(new ModelWarningRow { Key = "erase-test", Kind = "UnprovableUp", Message = "m", StartUtc = now });
            again.Glitches.Add(new GlitchRow
                { TargetId = target.Id, TargetName = "erase-target", StartUtc = now, EndUtc = now, LostCount = 1, SampleCount = 10 });
            await again.SaveChangesAsync();
        }

        // Factory reset: only builtin profiles and seeded settings remain.
        var reset = await _client.PostAsJsonAsync("/api/v1/admin/factory-reset", new { confirm = "RESET" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        await using (var after = await dbFactory.CreateDbContextAsync())
        {
            foreach (var (t, kind) in Classification)
            {
                if (kind is Kind.Measurement or Kind.Inbox
                    || t == typeof(TargetRow) || t == typeof(PoolRow) || t == typeof(PoolMemberRow))
                    Assert.True(await CountAsync(after, t) == 0, $"Factory reset left {t.Name} rows.");
            }
            Assert.Equal(builtinProfiles.Where(n => n != "Erase-test profile"),
                await after.Profiles.Select(p => p.Name).OrderBy(n => n).ToListAsync());
            Assert.Equal(seededSettings, await after.Settings.Select(s => s.Key).OrderBy(k => k).ToListAsync());
        }
    }
}
