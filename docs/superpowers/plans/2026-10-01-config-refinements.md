# Config Refinements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the config nav and the Admin page, give pools an on-page verdict anchor and a two-list member editor, replace the two built-in profiles with three described ones (Internal / Path / Public), and make the traceroute page classify hops and, when enabled, look up each hop's operator.

**Architecture:** Server-rendered Razor Pages with plain-JS helpers (no build step) over an ASP.NET minimal API; configuration in SQLite through EF Core; the engine's built-in profiles live in `ConCore`, their descriptions and seeding in `ConPersistence`. Hop classification is a pure function in `ConInfrastructure`; the ASN lookup is an interface there with a Team Cymru DNS implementation and a null implementation for tests. All UI work is verified in the preview app at http://localhost:18099 (see `.claude/launch.json`, config name `conwatch-preview`).

**Tech Stack:** .NET 10, EF Core 10 (SQLite), Razor Pages, Pico CSS v2, plain JavaScript, xunit, DnsClient 1.8.0 (new).

**Spec:** `docs/superpowers/specs/2026-10-01-config-refinements-design.md`

## Global Constraints

- Branch: `feature/config-refinements` (already created, off `feature/config-dialogs` at `2b716c7`). Never push.
- Windows 11, PowerShell is the default shell; run tests with `dotnet test`.
- Source files use CRLF line endings; write files as CRLF (the Write tool does; a heredoc from bash does not — convert if you used bash).
- Plain JavaScript only in `wwwroot/lib`; no React, no build step for these pages.
- Test rule from the repo: wait for conditions, never sleep-then-assert.
- `README.md` has UNCOMMITTED edits by Mark. Task 12 says exactly which lines you may touch. Never `git add README.md`; never run `git checkout -- README.md` or `git stash`.
- The API paths under `/api/v1/admin` keep their names. The "admin password" keeps its name.
- Built-in profile names are exactly `Internal`, `Path`, `Public`. `External` is removed, not aliased.
- Description limit: 500 characters after trim; empty becomes null.
- ASN lookup is OFF by default (`Traceroute.AsnLookup` = `false`) and never queries private, link-local, loopback, or CGNAT addresses.
- Commit after every task with the message given; no attribution lines.

## Review Focus

Inputs the spec implies but the tasks' own tests would not otherwise exercise. Each is pinned to a test in the owning task.

1. A traceroute whose destination is itself a private address (tracing to a LAN host): expected `Destination`, never `Internal`, and no ASN query. (Task 8 test `Private_destination_is_destination`; Task 10 test `Lookup_skips_private_and_unanswered_hops`.)
2. A Cymru TXT answer that lists two ASNs (`"7922 33667 | …"`): expected the first one, not a parse failure. (Task 9 test `Origin_answer_with_two_asns_takes_the_first`.)
3. An existing database whose user already has a custom profile named `Public`: expected External left alone and no crash on the unique index. (Task 3 test `Existing_custom_Public_leaves_External_alone`.)
4. A `PUT /api/v1/settings` with `Traceroute.AsnLookup` = `"yes"`: expected 400, not a stored garbage value. (Task 10 test `Asn_lookup_setting_accepts_only_booleans`.)
5. A pool dialog opened for Edit, members reordered, then cancelled: expected the next Add to start with an empty member list. (Task 7 browser check, step 7.)

---

### Task 1: Three built-in profiles in the engine

**Files:**
- Modify: `Core/ConCore/Targets/PingProfile.cs:20-24`
- Modify: `Core/ConEngine/Topology/TargetGraph.cs:38-42`
- Modify: `Core/ConEngine/Configuration/WatcherOptionsValidator.cs:122-125`
- Modify: `Core/ConEngine/Configuration/WatcherOptions.cs:10,26`
- Test: `Tests/ConEngine.Tests/PingProfileTests.cs`

**Interfaces:**
- Produces: `PingProfile.Internal`, `PingProfile.Path`, `PingProfile.Public` (static), `PingProfile.BuiltIns` (`IReadOnlyList<PingProfile>`). `PingProfile.External` no longer exists.

- [ ] **Step 1: Replace the test for the removed preset**

Replace the whole of `Tests/ConEngine.Tests/PingProfileTests.cs` with:

```csharp
using ConCore.Targets;

namespace ConEngine.Tests;

public class PingProfileTests
{
    [Fact]
    public void Internal_preset_pings_fast()
    {
        Assert.Equal("Internal", PingProfile.Internal.Name);
        Assert.Equal(TimeSpan.FromSeconds(1), PingProfile.Internal.NormalInterval);
    }

    [Fact]
    public void Path_preset_is_steady_but_not_lan_fast()
    {
        Assert.Equal("Path", PingProfile.Path.Name);
        Assert.Equal(TimeSpan.FromSeconds(5), PingProfile.Path.NormalInterval);
    }

    [Fact]
    public void Public_preset_pings_politely()
    {
        Assert.Equal("Public", PingProfile.Public.Name);
        Assert.Equal(TimeSpan.FromSeconds(10), PingProfile.Public.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), PingProfile.Public.BurstInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), PingProfile.Public.BurstMaxDuration);
    }

    [Fact]
    public void BuiltIns_lists_the_three_presets_in_order()
    {
        Assert.Equal(["Internal", "Path", "Public"], PingProfile.BuiltIns.Select(p => p.Name));
    }
}
```

- [ ] **Step 2: Run it to see it fail to compile**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingProfileTests"`
Expected: build error, `PingProfile` has no member `Path` / `Public` / `BuiltIns`.

- [ ] **Step 3: Change the presets**

In `Core/ConCore/Targets/PingProfile.cs` replace lines 20-24 with:

```csharp
    /// <summary>Preset for LAN targets: fast steady pinging is fine.</summary>
    public static PingProfile Internal { get; } = new() { Name = "Internal", NormalInterval = TimeSpan.FromSeconds(1) };

    /// <summary>Preset for hops on the route to the internet that the user does not
    /// own but may probe steadily — the ISP gateway, the modem.</summary>
    public static PingProfile Path { get; } = new() { Name = "Path", NormalInterval = TimeSpan.FromSeconds(5) };

    /// <summary>Preset for public internet sites: polite steady cadence.</summary>
    public static PingProfile Public { get; } = new() { Name = "Public", NormalInterval = TimeSpan.FromSeconds(10) };

    /// <summary>The presets every configuration has, in display order.</summary>
    public static IReadOnlyList<PingProfile> BuiltIns { get; } = [Internal, Path, Public];

    /// <summary>True for a built-in preset name, ignoring case.</summary>
    public static bool IsBuiltIn(string name) =>
        BuiltIns.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 4: Register all three in the graph and the validator**

In `Core/ConEngine/Topology/TargetGraph.cs` replace lines 38-42 with:

```csharp
        var profiles = PingProfile.BuiltIns.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
```

In `Core/ConEngine/Configuration/WatcherOptionsValidator.cs` replace lines 122-125 with:

```csharp
    private static bool IsKnownProfile(WatcherOptions options, string profileName) =>
        ConCore.Targets.PingProfile.IsBuiltIn(profileName) ||
        options.Profiles.ContainsKey(profileName);
```

In `Core/ConEngine/Configuration/WatcherOptions.cs`: line 10's comment becomes `/// <summary>Named custom profiles; built-ins "Internal", "Path" and "Public" always exist.</summary>` and line 26 becomes `public string Profile { get; set; } = "Public";`.

- [ ] **Step 5: Build the whole solution and fix every remaining `External` reference the compiler finds**

Run: `dotnet build ConnectionWatcher.sln`
Expected: errors in `Libraries/ConPersistence/Config/ConfigStore.cs:37` (`PingProfile.External`). Change that line to `Profiles = PingProfile.BuiltIns.Select(p => p.ToDto()).ToList(),` for now (Task 2 replaces it again) and change lines 52-54 to:

```csharp
                    if (PingProfile.IsBuiltIn(name))
                        continue; // builtins already present; file overrides not applied at seed
```

Build again until clean.

- [ ] **Step 6: Rename `External` to `Public` in test data**

These tests use the profile name as data, and the name no longer exists. Replace the string `"External"` with `"Public"` on exactly these lines:
- `Tests/ConPersistence.Tests/ConfigStoreTests.cs:44` and `:90`. On line 43-44 also change `Assert.Equal(2, options.Profiles.Count);` to `Assert.Equal(3, options.Profiles.Count);` and add after line 44: `Assert.Equal(5.0, options.Profiles["Path"].NormalIntervalSeconds);`
- `Tests/ConPersistence.Tests/ConfigServiceTests.cs:189`
- `Tests/ConWatch.Tests/OperatorFieldTests.cs:32` and `:88`
- `Tests/ConWatch.Tests/ProbeFieldTests.cs:30, 53, 73, 86`
- `Tests/ConWatch.Tests/RuntimeHealthTests.cs:25, 66`
- `Tests/ConWatch.Tests/SnapshotTests.cs:30`

- [ ] **Step 7: Run the engine and persistence tests**

Run: `dotnet test Tests/ConEngine.Tests; dotnet test Tests/ConPersistence.Tests`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add Core Libraries/ConPersistence/Config/ConfigStore.cs Tests
git commit -m "feat(engine): built-in profiles Internal, Path, Public replace Internal/External"
```

---

### Task 2: Profile descriptions in persistence, seeding, and the API

**Files:**
- Create: `Libraries/ConPersistence/Config/BuiltInProfiles.cs`
- Modify: `Libraries/ConPersistence/Entities/ConfigRows.cs:7-23`
- Modify: `Libraries/ConPersistence/Config/ConfigDtos.cs:8-24`
- Modify: `Libraries/ConPersistence/Config/ConfigMapping.cs:28-34,37-50,87-102`
- Modify: `Libraries/ConPersistence/Config/ConfigStore.cs:33-67` (+ new method)
- Modify: `Libraries/ConPersistence/Config/ConfigService.cs:43-60,236-252`
- Test: `Tests/ConPersistence.Tests/ConfigStoreTests.cs`, `Tests/ConPersistence.Tests/ConfigServiceTests.cs`

**Interfaces:**
- Consumes: `PingProfile.BuiltIns`, `PingProfile.IsBuiltIn` (Task 1).
- Produces: `ProfileDto.Description` (`string?`), `ProfileRow.Description` (`string?`), `BuiltInProfiles.All` (`IReadOnlyList<BuiltInProfiles.Entry>` with `Profile` and `Description`), `ConfigStore.EnsureBuiltInsAsync()`, `ConfigMapping.NormalizeDescription(string?)`, `ConfigService.MaxDescriptionLength` (500).

- [ ] **Step 1: Write the failing store tests**

Append to `Tests/ConPersistence.Tests/ConfigStoreTests.cs` inside the class:

```csharp
    [Fact]
    public async Task Seed_gives_every_builtin_a_description()
    {
        await _store.SeedAsync(null);
        var doc = await _store.ExportAsync();
        Assert.Equal(3, doc.Profiles.Count);
        Assert.All(doc.Profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        Assert.Contains("LAN", doc.Profiles.Single(p => p.Name == "Internal").Description);
    }

    [Fact]
    public async Task EnsureBuiltIns_adds_missing_presets_and_fills_null_descriptions_only()
    {
        // A database from before Path existed: Internal with no description, a custom profile.
        await using (var db = _factory.CreateDbContext())
        {
            db.Profiles.Add(new ConPersistence.Entities.ProfileRow
                { Id = Guid.NewGuid(), Name = "Internal", NormalIntervalSeconds = 2, Description = null });
            db.Profiles.Add(new ConPersistence.Entities.ProfileRow
                { Id = Guid.NewGuid(), Name = "Public", Description = "mine" });
            db.Profiles.Add(new ConPersistence.Entities.ProfileRow
                { Id = Guid.NewGuid(), Name = "Slow", NormalIntervalSeconds = 30 });
            await db.SaveChangesAsync();
        }

        await _store.EnsureBuiltInsAsync();

        var doc = await _store.ExportAsync();
        Assert.Equal(["Internal", "Path", "Public", "Slow"], doc.Profiles.Select(p => p.Name).OrderBy(n => n));
        var internalProfile = doc.Profiles.Single(p => p.Name == "Internal");
        Assert.Equal(2, internalProfile.NormalIntervalSeconds); // numbers never overwritten
        Assert.Contains("LAN", internalProfile.Description);     // null description filled
        Assert.Equal("mine", doc.Profiles.Single(p => p.Name == "Public").Description); // kept
        Assert.Equal(5, doc.Profiles.Single(p => p.Name == "Path").NormalIntervalSeconds);
        Assert.Null(doc.Profiles.Single(p => p.Name == "Slow").Description);
    }
```

- [ ] **Step 2: Write the failing service tests**

Find the test class setup in `Tests/ConPersistence.Tests/ConfigServiceTests.cs` (it has `_store`, `_service`, and seeds `ConfigStoreTests.ValidHome()` in its constructor). Append inside the class:

```csharp
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
```

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~ConfigStoreTests|FullyQualifiedName~ConfigServiceTests"`
Expected: build errors (`Description` does not exist, `EnsureBuiltInsAsync` does not exist).

- [ ] **Step 4: Add the column, the DTO field, the mapping, and the normalizer**

`Libraries/ConPersistence/Entities/ConfigRows.cs`: after line 11 (`public string Name ...`) insert:

```csharp
    /// <summary>What this profile is for, shown beside its name. Null when the user left it blank.</summary>
    public string? Description { get; set; }
```

`Libraries/ConPersistence/Config/ConfigDtos.cs`: after line 13 (`public required string Name ...`) insert:

```csharp
    /// <summary>What this profile is for; trimmed, at most 500 characters, blank stored as null.</summary>
    public string? Description { get; init; }
```

`Libraries/ConPersistence/Config/ConfigMapping.cs`:
- after the `NormalizeOperator` method (line 30) add:

```csharp
    /// <summary>Blank descriptions are stored as null; otherwise trimmed.</summary>
    public static string? NormalizeDescription(string? description) =>
        description?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
```

- in `ToDto(this ProfileRow p)` (line 39) change `Id = p.Id, Name = p.Name,` to `Id = p.Id, Name = p.Name, Description = p.Description,`.
- in `CopyFrom(this ProfileRow row, ProfileDto dto)` after `row.Name = dto.Name;` add `row.Description = NormalizeDescription(dto.Description);`.
- `ToDto(this PingProfile p, ...)` is unchanged (engine presets carry no description).

- [ ] **Step 5: Create `BuiltInProfiles`**

Create `Libraries/ConPersistence/Config/BuiltInProfiles.cs`:

```csharp
using ConCore.Targets;

namespace ConPersistence.Config;

/// <summary>
/// The engine's built-in presets paired with the descriptions the UI shows.
/// The engine never displays a description, so the text lives here, beside
/// the seeding code, rather than on <see cref="PingProfile"/>.
/// </summary>
public static class BuiltInProfiles
{
    public sealed record Entry(PingProfile Profile, string Description)
    {
        public ProfileDto ToDto() => Profile.ToDto() with { Description = Description };
    }

    public static readonly IReadOnlyList<Entry> All =
    [
        new(PingProfile.Internal, "Devices on your own LAN. Probe as hard as you like."),
        new(PingProfile.Path, "Hops on your route to the internet that you don't own but may " +
                              "probe steadily, such as the ISP gateway."),
        new(PingProfile.Public, "Public internet sites. Be polite."),
    ];
}
```

- [ ] **Step 6: Seed from it and add `EnsureBuiltInsAsync`**

In `Libraries/ConPersistence/Config/ConfigStore.cs` change line 37 to:

```csharp
            Profiles = BuiltInProfiles.All.Select(e => e.ToDto()).ToList(),
```

After the `SeedAsync` method (after line 67) add:

```csharp
    /// <summary>
    /// Makes sure every built-in preset exists and carries a description.
    /// Runs on every start, after migrations: an older database gains the
    /// presets added since it was created (Path, as of 2026-10), and a
    /// deleted preset comes back. Numbers and non-null descriptions are
    /// never overwritten — the user's edits win.
    /// </summary>
    public async Task EnsureBuiltInsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Profiles.ToListAsync();
        foreach (var builtIn in BuiltInProfiles.All)
        {
            var row = rows.FirstOrDefault(r =>
                r.Name.Equals(builtIn.Profile.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                db.Profiles.Add(builtIn.ToDto().ToRow(Guid.NewGuid()));
                _log.LogInformation("Added missing built-in profile '{Profile}'", builtIn.Profile.Name);
            }
            else if (row.Description is null)
            {
                row.Description = builtIn.Description;
            }
        }
        await db.SaveChangesAsync();
    }
```

- [ ] **Step 7: Validate the description in upsert and import**

In `Libraries/ConPersistence/Config/ConfigService.cs`, inside the class after the `AddressChangeMarkers` field (after line 38) add:

```csharp
    public const int MaxDescriptionLength = 500;

    private static string? DescriptionError(ProfileDto dto) =>
        ConfigMapping.NormalizeDescription(dto.Description) is { Length: > MaxDescriptionLength }
            ? $"Profile '{dto.Name}': description is longer than {MaxDescriptionLength} characters."
            : null;
```

In `UpsertProfileAsync` (line 43) make the first statement inside the lambda:

```csharp
        if (DescriptionError(dto) is { } descriptionError)
            return ConfigResult.Fail(ConfigErrorKind.Validation, descriptionError);
```

In `ImportAsync` (line 236), before the `WatcherOptionsValidator` line add:

```csharp
        var descriptionErrors = doc.Profiles.Select(DescriptionError).Where(e => e is not null).ToArray();
        if (descriptionErrors.Length > 0)
            return ConfigResult.Fail(ConfigErrorKind.Validation, descriptionErrors!);
```

- [ ] **Step 8: Run the persistence tests**

Run: `dotnet test Tests/ConPersistence.Tests`
Expected: all pass. (The test DB uses `EnsureCreated`, so the new column exists without a migration; Task 3 adds the migration for real databases.)

- [ ] **Step 9: Commit**

```bash
git add Libraries/ConPersistence Tests/ConPersistence.Tests
git commit -m "feat(persistence): profile descriptions, built-in presets with text, EnsureBuiltInsAsync"
```

---

### Task 3: Migration `AddProfileDescription` and the startup hook

**Files:**
- Create: `Libraries/ConPersistence/Migrations/<stamp>_AddProfileDescription.cs` (+ `.Designer.cs`, generated)
- Modify: `Libraries/ConPersistence/Migrations/ConWatchDbContextModelSnapshot.cs` (generated)
- Modify: `Programs/ConWatch/WatcherHostService.cs:92-101`
- Test: `Tests/ConPersistence.Tests/ProfileMigrationTests.cs` (new)

**Interfaces:**
- Consumes: `ConfigStore.EnsureBuiltInsAsync()` (Task 2).

- [ ] **Step 1: Generate the migration**

Run from the repo root:

```powershell
dotnet tool restore
dotnet ef migrations add AddProfileDescription --project Libraries/ConPersistence --startup-project Programs/ConWatch
```

Expected: three files change under `Libraries/ConPersistence/Migrations`. Open the new `<stamp>_AddProfileDescription.cs`; `Up` should contain one `AddColumn<string>("Description", "Profiles", "TEXT", nullable: true)`.

- [ ] **Step 2: Add the rename to `Up`**

Edit the generated `Up` so it reads:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            // The built-in preset "External" became "Public" (2026-10). Targets
            // reference profiles by id, so renaming the row carries them along.
            // Guarded so a user who already made a custom "Public" keeps both.
            // Name has NOCASE collation, so the comparison ignores case.
            migrationBuilder.Sql(
                "UPDATE Profiles SET Name = 'Public' WHERE Name = 'External' " +
                "AND NOT EXISTS (SELECT 1 FROM Profiles WHERE Name = 'Public');");
        }
```

`Down` stays as generated (drops the column).

- [ ] **Step 3: Write the failing migration test**

Create `Tests/ConPersistence.Tests/ProfileMigrationTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ConPersistence.Config;

namespace ConPersistence.Tests;

/// <summary>
/// Runs the real migration chain against a file database frozen at the
/// migration before AddProfileDescription, seeded the way a 2026-09 install
/// looked, then migrates forward. The in-memory TestDbFactory uses
/// EnsureCreated and cannot exercise this.
/// </summary>
public class ProfileMigrationTests : IDisposable
{
    private const string BeforeDescription = "20260915162855_AddTargetProbe";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"conwatch-mig-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<ConWatchDbContext> _options;

    public ProfileMigrationTests() =>
        _options = new DbContextOptionsBuilder<ConWatchDbContext>().UseSqlite($"Data Source={_path}").Options;

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_path}"));
        File.Delete(_path);
    }

    private sealed class Factory(DbContextOptions<ConWatchDbContext> options) : IDbContextFactory<ConWatchDbContext>
    {
        public ConWatchDbContext CreateDbContext() => new(options);
    }

    private static string ProfileInsert(string name, double interval) =>
        $"INSERT INTO Profiles (Id, Name, NormalIntervalSeconds, PingTimeoutSeconds, BurstIntervalSeconds, " +
        $"BurstMaxDurationSeconds, HistorySize, MinUsableSample, RespondingThreshold, FalteringThreshold, " +
        $"DownThreshold, RecoveringThreshold) VALUES ('{Guid.NewGuid().ToString().ToUpperInvariant()}', " +
        $"'{name}', {interval}, 3, 1, 60, 100, 10, 0.95, 0.75, 0.10, 0.25);";

    private async Task SeedOldInstallAsync(params (string Name, double Interval)[] profiles)
    {
        await using var db = new ConWatchDbContext(_options);
        await db.GetService<IMigrator>().MigrateAsync(BeforeDescription);
        foreach (var (name, interval) in profiles)
            await db.Database.ExecuteSqlRawAsync(ProfileInsert(name, interval));
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Targets (Id, Name, Address, ProfileId, Enabled) " +
            "SELECT '" + Guid.NewGuid().ToString().ToUpperInvariant() + "', 'Google', '8.8.8.8', Id, 1 " +
            "FROM Profiles WHERE Name = 'External';");
    }

    [Fact]
    public async Task External_becomes_Public_and_Path_is_added()
    {
        await SeedOldInstallAsync(("Internal", 1), ("External", 10));

        await using (var db = new ConWatchDbContext(_options))
            await db.Database.MigrateAsync();
        var store = new ConfigStore(new Factory(_options));
        await store.EnsureBuiltInsAsync();

        var doc = await store.ExportAsync();
        Assert.Equal(["Internal", "Path", "Public"], doc.Profiles.Select(p => p.Name).OrderBy(n => n));
        Assert.Equal("Public", doc.Targets.Single(t => t.Name == "Google").Profile);
        Assert.Equal(10, doc.Profiles.Single(p => p.Name == "Public").NormalIntervalSeconds);
        Assert.All(doc.Profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public async Task Existing_custom_Public_leaves_External_alone()
    {
        await SeedOldInstallAsync(("Internal", 1), ("External", 10), ("Public", 20));

        await using (var db = new ConWatchDbContext(_options))
            await db.Database.MigrateAsync();
        var store = new ConfigStore(new Factory(_options));
        await store.EnsureBuiltInsAsync();

        var doc = await store.ExportAsync();
        Assert.Equal(["External", "Internal", "Path", "Public"], doc.Profiles.Select(p => p.Name).OrderBy(n => n));
        Assert.Equal("External", doc.Targets.Single(t => t.Name == "Google").Profile);
        Assert.Equal(20, doc.Profiles.Single(p => p.Name == "Public").NormalIntervalSeconds);
    }
}
```

- [ ] **Step 4: Run the migration tests**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~ProfileMigrationTests"`
Expected: both pass. If `MigrateAsync(BeforeDescription)` complains the name is unknown, list the migrations with `dotnet ef migrations list --project Libraries/ConPersistence --startup-project Programs/ConWatch` and use the exact id printed.

- [ ] **Step 5: Call `EnsureBuiltInsAsync` at startup**

In `Programs/ConWatch/WatcherHostService.cs` replace lines 92-101 with:

```csharp
        // Schema migration happened in DatabaseStartup.PrepareAsync before any
        // hosted service started; this only seeds an empty config, then makes
        // sure every built-in preset exists (an older database gains Path).
        if (await store.IsEmptyAsync())
        {
            var seed = configuration.GetSection(WatcherOptions.SectionName).Exists()
                ? configuration.GetSection(WatcherOptions.SectionName).Get<WatcherOptions>()
                : null;
            await store.SeedAsync(seed);
        }
        await store.EnsureBuiltInsAsync();
```

- [ ] **Step 6: Run the full suite**

Run: `dotnet test ConnectionWatcher.sln`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add Libraries/ConPersistence/Migrations Programs/ConWatch/WatcherHostService.cs Tests/ConPersistence.Tests/ProfileMigrationTests.cs
git commit -m "feat(persistence): AddProfileDescription migration renames External to Public; ensure built-ins at startup"
```

---

### Task 4: Public as the default everywhere in the web app; descriptions on the Profiles page

**Files:**
- Modify: `Programs/ConWatch/SetupService.cs:56,68,81`
- Modify: `Programs/ConWatch/Pages/Setup.cshtml:65`
- Modify: `Programs/ConWatch/Pages/Config/Targets.cshtml:113-120`
- Modify: `Programs/ConWatch/Pages/Config/Profiles.cshtml:17-50,` dialog form, and script
- Test: `Tests/ConWatch.Tests/SetupApiTests.cs`, `Tests/ConWatch.Tests/ConfigApiTests.cs`, `Tests/ConWatch.Tests/PageSmokeTests.cs`

**Interfaces:**
- Consumes: `ProfileDto.Description` (Task 2).

- [ ] **Step 1: Write the failing wizard test**

Append inside the class in `Tests/ConWatch.Tests/SetupApiTests.cs`:

```csharp
    [Fact]
    public async Task Apply_assigns_Internal_Path_and_Public_by_role()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, new SetupTarget("WAN", "203.0.113.23"), ["8.8.8.8"]));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        Assert.Equal("Internal", targets!.Single(t => t.Name == "Router").Profile);
        Assert.Equal("Path", targets.Single(t => t.Address == "203.0.113.23").Profile);
        Assert.Equal("Public", targets.Single(t => t.Address == "8.8.8.8").Profile);
    }
```

- [ ] **Step 2: Write the failing API round-trip test**

Append inside the class in `Tests/ConWatch.Tests/ConfigApiTests.cs`:

```csharp
    [Fact]
    public async Task Profile_description_round_trips_through_the_api_and_export()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/profiles",
            new ProfileDto { Name = "Described", Description = " Mine. " });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var profiles = await _client.GetFromJsonAsync<List<ProfileDto>>("/api/v1/profiles");
        var described = profiles!.Single(p => p.Name == "Described");
        Assert.Equal("Mine.", described.Description);

        var update = await _client.PutAsJsonAsync($"/api/v1/profiles/{described.Id}",
            described with { Description = "Changed." });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var exported = await _client.GetFromJsonAsync<ConfigDocument>("/api/v1/config/export");
        Assert.Equal("Changed.", exported!.Profiles.Single(p => p.Name == "Described").Description);

        var tooLong = await _client.PostAsJsonAsync("/api/v1/profiles",
            new ProfileDto { Name = "Long", Description = new string('x', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Fresh_install_has_three_described_builtins()
    {
        var profiles = await _client.GetFromJsonAsync<List<ProfileDto>>("/api/v1/profiles");
        Assert.Equal(["Internal", "Path", "Public"], profiles!.Select(p => p.Name).OrderBy(n => n));
        Assert.All(profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }
```

Check how `ExportApiTests.cs` fetches the export (it may use a different path than `/api/v1/config/export`); match it.

- [ ] **Step 3: Add page markers to the smoke test**

In `Tests/ConWatch.Tests/PageSmokeTests.cs` add two `InlineData` lines to `Page_renders`:

```csharp
    [InlineData("/Config/Profiles", "Be polite")]           // built-in description in the table
    [InlineData("/Config/Targets", "option selected=\"selected\" title=\"Public internet sites")] // the Public default's title
```

If the second marker proves brittle against Razor's attribute rendering, replace it with `[InlineData("/Config/Targets", "Public internet sites")]`.

- [ ] **Step 4: Run them to see them fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~SetupApiTests|FullyQualifiedName~ConfigApiTests.Profile_description|FullyQualifiedName~ConfigApiTests.Fresh_install|FullyQualifiedName~PageSmokeTests"`
Expected: the wizard test fails (WAN hop is Internal, anchor is External → unknown profile), the smoke markers fail; the API round-trip may already pass.

- [ ] **Step 5: Wizard and copy**

`Programs/ConWatch/SetupService.cs`: line 68 `Profile = "Internal",` (the WAN hop) → `Profile = "Path",`; line 81 `Profile = "External",` → `Profile = "Public",`. Line 56 (router) stays `Internal`.

`Programs/ConWatch/Pages/Setup.cshtml:65`: `polite External cadence` → `polite Public cadence`.

- [ ] **Step 6: Targets dialog: Public default, description as tooltip**

In `Programs/ConWatch/Pages/Config/Targets.cshtml` replace the Profile `<label>` block (lines 113-120) with:

```cshtml
                <label>Profile
                    <select name="profile">
                        @foreach (var p in Model.Config.Profiles.OrderBy(p => p.Name))
                        {
                            <option selected="@(p.Name == "Public")" title="@p.Description">@p.Name</option>
                        }
                    </select>
                </label>
```

(Task 11 changes the `selected` expression again to honour a prefill.)

- [ ] **Step 7: Profiles page: column and textarea**

In `Programs/ConWatch/Pages/Config/Profiles.cshtml`:
- table header: `<th>Name</th><th>Description</th><th>Normal</th>…`
- table row: after `<td>@p.Name</td>` add `<td class="cw-muted">@p.Description</td>`
- in the dialog form, after the `Name` label add:

```cshtml
            <label>Description <small>(optional — what this profile is for)</small>
                <textarea name="description" rows="2" maxlength="500"></textarea>
            </label>
```

- in the script's `buildDto` change `const dto = { name: form.elements.name.value };` to `const dto = { name: form.elements.name.value, description: form.elements.description.value || null };`
- in `fillForm` add `form.elements.description.value = dto.description ?? "";` after the name line.

Add to `Programs/ConWatch/wwwroot/css/site.css` after the `.cw-actions` rule:

```css
.cw-muted { color: var(--muted); font-size: 0.85rem; }
```

- [ ] **Step 8: Run the web tests**

Run: `dotnet test Tests/ConWatch.Tests`
Expected: all pass.

- [ ] **Step 9: Browser check**

Start the preview (`preview_start` with name `conwatch-preview`), open `/Config/Profiles`: the Description column shows the three built-in texts; Edit on Internal shows the text in the textarea; change it, Save, the table shows the new text. Open `/Config/Targets`, Add target: Profile defaults to Public; hovering an option shows its description. Check the console for errors.

- [ ] **Step 10: Commit**

```bash
git add Programs/ConWatch Tests/ConWatch.Tests
git commit -m "feat(web): Public is the default profile; wizard uses Internal/Path/Public; descriptions on the Profiles page"
```

---

### Task 5: Nav renames — Config, Settings, redirect, tray

**Files:**
- Modify: `Programs/ConWatch/Pages/_Layout.cshtml:36-42`
- Rename: `Programs/ConWatch/Pages/Admin.cshtml` → `Settings.cshtml`; `Admin.cshtml.cs` → `Settings.cshtml.cs`
- Rename: `Programs/ConWatch/wwwroot/lib/site-admin.js` → `site-settings.js`
- Modify: `Programs/ConWatch/Startup/ConWatchServices.cs:143`
- Modify: `Programs/ConWatch/ConWatchApp.cs:78` (after `app.MapRazorPages();`)
- Modify: `Programs/ConWatchTray/Program.cs:47,49`
- Modify: `Programs/ConWatch/Pages/Data.cshtml.cs:7`, `Setup.cshtml.cs:5`, `Traceroute.cshtml.cs:5` (doc comments only)
- Test: `Tests/ConWatch.Tests/PageSmokeTests.cs`, `Tests/ConWatch.Tests/AdminLockTests.cs`

- [ ] **Step 1: Update the smoke tests**

In `Tests/ConWatch.Tests/PageSmokeTests.cs` change the four `"/Admin"` InlineData lines to `"/Settings"`, and add:

```csharp
    [InlineData("/Settings", "<h2>Settings</h2>")]
```

Add a new test in the same class:

```csharp
    [Fact]
    public async Task Old_admin_url_redirects_permanently_to_settings()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Admin");
        Assert.Equal(System.Net.HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/Settings", response.Headers.Location?.ToString());
    }
```

Open `Tests/ConWatch.Tests/AdminLockTests.cs`; wherever it requests `/Admin` as a locked page, change it to `/Settings`.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~PageSmokeTests|FullyQualifiedName~AdminLockTests"`
Expected: `/Settings` returns 404.

- [ ] **Step 3: Rename the page**

```powershell
git mv Programs/ConWatch/Pages/Admin.cshtml Programs/ConWatch/Pages/Settings.cshtml
git mv Programs/ConWatch/Pages/Admin.cshtml.cs Programs/ConWatch/Pages/Settings.cshtml.cs
git mv Programs/ConWatch/wwwroot/lib/site-admin.js Programs/ConWatch/wwwroot/lib/site-settings.js
```

In `Settings.cshtml.cs`: `AdminModel` → `SettingsModel`; the doc comment → `/// <summary>Admin-locked by convention (AdminLockPageFilter in ConWatchServices).</summary>`.

In `Settings.cshtml`:
- line 4: `@model ConWatch.Pages.SettingsModel`
- line 6: `ViewData["Title"] = "Settings — ConnectionWatcher";`
- lines 11-13:

```cshtml
        <h2>Settings</h2>
        <p>Node controls: monitoring, notifications, and the danger zone.
           Statistics, exports, and retention live on the <a href="/Data">Data</a> page;
           the verdict anchor is chosen on the <a href="/Config/Pools">Pools</a> page.</p>
```

- line 220: `<script src="~/lib/site-settings.js" asp-append-version="true"></script>`
- line 1 of `site-settings.js`: `// Settings page logic (plain JS, no build step).`

(The Verdict section itself leaves this page in Task 6.)

- [ ] **Step 4: Nav, filter, redirect, tray**

`Programs/ConWatch/Pages/_Layout.cshtml` lines 36-42:

```csharp
    var manageNav = new (string Key, string Href, string Label, string ActivePrefix)[]
    {
        ("data", "/Data", "Data", "/Data"),
        ("targets", "/Config/Targets", "Config", "/Config"),
        ("traceroute", "/Traceroute", "Traceroute", "/Traceroute"),
        ("admin", "/Settings", "Settings", "/Settings"),
    };
```

`Programs/ConWatch/Startup/ConWatchServices.cs:143`:

```csharp
            foreach (var page in new[] { "/Settings", "/Data", "/Setup", "/Traceroute" })
```

`Programs/ConWatch/ConWatchApp.cs`, right after `app.MapRazorPages();`:

```csharp
        // The Admin page became Settings (2026-10); keep old bookmarks working.
        app.MapGet("/Admin", () => Results.Redirect("/Settings", permanent: true));
```

`Programs/ConWatchTray/Program.cs`: line 47 becomes `menu.Items.Add("Server settings", null, (_, _) => Open("/Settings"));` and line 49 becomes `menu.Items.Add("Tray settings…", null, (_, _) => ShowSettings());`. (The web page took the name Settings; the tray's own dialog is renamed so the two are told apart.)

Doc comments in `Data.cshtml.cs`, `Setup.cshtml.cs`, `Traceroute.cshtml.cs`: `(AdminLockPageFilter in Program.cs)` → `(AdminLockPageFilter in ConWatchServices)`.

- [ ] **Step 5: Build everything and run the web tests**

Run: `dotnet build ConnectionWatcher.sln; dotnet test Tests/ConWatch.Tests`
Expected: clean build, all pass.

- [ ] **Step 6: Browser check**

Reload the preview. The top bar reads Dashboard, History | Data, Config, Traceroute, Settings. Config is highlighted on all three config tabs. `/Admin` lands on `/Settings`. The drawer at phone width shows the same labels.

- [ ] **Step 7: Commit**

```bash
git add -A Programs/ConWatch Programs/ConWatchTray Tests/ConWatch.Tests
git commit -m "feat(web): nav entry Config; Admin page becomes Settings with a permanent redirect"
```

---

### Task 6: Verdict section and anchor badge on the Pools page

**Files:**
- Modify: `Programs/ConWatch/Pages/Config/Pools.cshtml.cs`
- Modify: `Programs/ConWatch/Pages/Config/Pools.cshtml:17-36`
- Modify: `Programs/ConWatch/Pages/Settings.cshtml:61-80` (remove the Verdict form)
- Modify: `Programs/ConWatch/wwwroot/lib/site-config.js` (add `wireSettingsForm`)
- Modify: `Programs/ConWatch/wwwroot/lib/site-settings.js:5-19`
- Test: `Tests/ConWatch.Tests/PageSmokeTests.cs`

**Interfaces:**
- Produces: `wireSettingsForm(formId, errorsId)` in `site-config.js` (moved from `site-settings.js`), used by both pages.

- [ ] **Step 1: Smoke markers**

In `PageSmokeTests.Page_renders` add:

```csharp
    [InlineData("/Config/Pools", "Anchor pool")]
    [InlineData("/Config/Pools", "Anchors needed for Healthy")]
```

And a test that the badge follows the setting:

```csharp
    [Fact]
    public async Task Pools_page_badges_the_anchor_pool()
    {
        var client = _factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);          // creates pool "Internet"; AnchorPool is "Internet" from appsettings
        var html = await client.GetStringAsync("/Config/Pools");
        Assert.Contains("cw-chip\">anchor</span>", html);
        await client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.AnchorPool"] = null });
        html = await client.GetStringAsync("/Config/Pools");
        Assert.DoesNotContain("cw-chip\">anchor</span>", html);
    }
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~PageSmokeTests"`
Expected: new markers missing.

- [ ] **Step 3: Page model**

Replace `Programs/ConWatch/Pages/Config/Pools.cshtml.cs` with:

```csharp
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>Admin-locked by convention (AdminLockPageFilter on /Config).</summary>
public class PoolsModel(ConfigStore store, ConfigHealth health, SettingsStore settings) : PageModel
{
    public ConfigDocument Config { get; private set; } = new();
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];
    /// <summary>The Verdict.AnchorPool setting; null or empty means the verdict layer is off.</summary>
    public string? AnchorPool { get; private set; }
    public int WeatherThreshold { get; private set; }

    public bool IsAnchor(PoolDto pool) =>
        AnchorPool is { Length: > 0 } && pool.Name.Equals(AnchorPool, StringComparison.OrdinalIgnoreCase);

    public async Task OnGetAsync()
    {
        Config = await store.ExportAsync();
        Warnings = await health.EvaluateAsync();
        AnchorPool = await settings.GetAsync(SettingKeys.AnchorPool);
        WeatherThreshold = await settings.GetIntAsync(SettingKeys.WeatherThreshold, SettingsDefaults.WeatherThreshold);
    }
}
```

- [ ] **Step 4: Page markup**

In `Programs/ConWatch/Pages/Config/Pools.cshtml` add `@using ConPersistence.Settings` under line 2, then replace lines 17-36 (the intro `<p>` and the table) with:

```cshtml
<p>Rotation pools: members share the pinging load by phase-stagger; pool state is derived (any member responding → responding). Member order sets the stagger offsets.</p>

<h3>Verdict</h3>
<p>The verdict reads the members of one pool, the internet anchors. Other pools only
   share pinging load and report a combined state.</p>
<ul class="form-errors" id="verdict-errors"></ul>
<form id="verdict-form">
    <div class="grid">
        <label>Anchor pool
            <select name="@SettingKeys.AnchorPool">
                <option value="">(none — verdict off)</option>
                @foreach (var pool in Model.Config.Pools.OrderBy(p => p.Name))
                {
                    <option selected="@Model.IsAnchor(pool)">@pool.Name</option>
                }
            </select>
        </label>
        <label>Anchors needed for Healthy
            <input name="@SettingKeys.WeatherThreshold" type="number" min="1" value="@Model.WeatherThreshold" />
        </label>
        <div style="align-self: end"><button type="submit">Save verdict settings</button></div>
    </div>
</form>

<h3>Pools</h3>
<table>
    <thead><tr><th>Name</th><th>Members (in order)</th><th>Parent</th><th></th><th></th></tr></thead>
    <tbody>
    @foreach (var p in Model.Config.Pools.OrderBy(p => p.Name))
    {
        <tr>
            <td>@p.Name</td>
            <td>@string.Join(" → ", p.Members)</td>
            <td>@(p.Parent ?? "—")</td>
            <td>@if (Model.IsAnchor(p)) { <span class="cw-chip">anchor</span> }</td>
            <td>
                <button class="outline" data-edit="@p.Id" data-json="@json(p)">Edit</button>
                <button class="outline" data-delete="@p.Id" data-name="@p.Name">Delete</button>
            </td>
        </tr>
    }
    </tbody>
</table>
```

At the end of the page script (inside `@section Scripts`, after `wireConfigPage({...});`) add `wireSettingsForm("verdict-form", "verdict-errors");`.

- [ ] **Step 5: Share `wireSettingsForm`**

Cut lines 5-17 of `site-settings.js` (the `wireSettingsForm` function) and paste them into `site-config.js` just before `function wireDialog`, with the comment `// Posts every [name] field of a form to the settings API; empty means "clear".` In `site-settings.js` delete line 18 (`wireSettingsForm("settings-form", "settings-errors");`) and keep line 19 (`notify-form`).

- [ ] **Step 6: Remove the Verdict form from Settings**

In `Programs/ConWatch/Pages/Settings.cshtml` delete lines 61-80 (`<h3>Settings</h3>` through `</form>`). In `Settings.cshtml.cs` delete the `Pools` property and its load line, and the `using ConPersistence.Config;` if nothing else needs it (build will tell you). (Task 10 adds a Traceroute section to this page.)

- [ ] **Step 7: Run the web tests, then the browser check**

Run: `dotnet test Tests/ConWatch.Tests`
Expected: all pass.

Browser: `/Config/Pools` shows the Verdict section above the table; the Internet pool carries the `anchor` chip; choosing "(none)" and saving reloads with no chip and the health banner warns "No anchor pool"; choosing Internet again restores it. `/Settings` no longer shows an anchor-pool select.

- [ ] **Step 8: Commit**

```bash
git add Programs/ConWatch Tests/ConWatch.Tests
git commit -m "feat(web): verdict anchor pool and threshold are chosen on the Pools page"
```

---

### Task 7: Two-list member editor in the pool dialog

**Files:**
- Modify: `Programs/ConWatch/Pages/Config/Pools.cshtml` (dialog form + script)
- Modify: `Programs/ConWatch/wwwroot/css/site.css`

There is no unit-test harness for the page scripts; this task is verified in the browser (steps 4-7), including Review Focus item 5.

- [ ] **Step 1: Replace the Members fieldset**

In the pool dialog's `<form id="pool-form">`, replace the `<fieldset>` … `</fieldset>` block with:

```cshtml
            <fieldset class="cw-duallist">
                <legend>Members <small>(in stagger order)</small></legend>
                <div class="cw-duallist-grid">
                    <label>Available
                        <select id="available-list" multiple size="7" aria-label="Available targets">
                            @foreach (var t in Model.Config.Targets.OrderBy(t => t.Name))
                            {
                                <option>@t.Name</option>
                            }
                        </select>
                    </label>
                    <div class="cw-duallist-buttons">
                        <button type="button" class="outline" id="member-add" title="Add selected">Add →</button>
                        <button type="button" class="outline" id="member-remove" title="Remove selected">← Remove</button>
                        <button type="button" class="outline" id="member-up" title="Move up">↑</button>
                        <button type="button" class="outline" id="member-down" title="Move down">↓</button>
                    </div>
                    <label>Members
                        <select id="member-list" multiple size="7" aria-label="Pool members in stagger order"></select>
                    </label>
                </div>
                <small>Member order sets the ping stagger: member <i>k</i> of <i>n</i> pings
                    <i>k/n</i> of the interval after member 1.</small>
            </fieldset>
```

- [ ] **Step 2: Replace the member script**

In the page script, replace everything from `const memberList = document.getElementById("member-list");` through the `document.querySelectorAll("[data-add-member]")…` block with:

```javascript
        const allTargets = @Html.Raw(json(Model.Config.Targets.OrderBy(t => t.Name).Select(t => t.Name)));
        const availableList = document.getElementById("available-list");
        const memberList = document.getElementById("member-list");
        let members = [];

        // `members` is the source of truth; both lists are rebuilt from it.
        function renderMembers() {
            const selectedMembers = new Set([...memberList.selectedOptions].map((o) => o.value));
            const selectedAvailable = new Set([...availableList.selectedOptions].map((o) => o.value));
            memberList.innerHTML = "";
            for (const name of members) {
                const o = new Option(name, name);
                o.selected = selectedMembers.has(name);
                memberList.add(o);
            }
            availableList.innerHTML = "";
            for (const name of allTargets.filter((n) => !members.includes(n))) {
                const o = new Option(name, name);
                o.selected = selectedAvailable.has(name);
                availableList.add(o);
            }
            const idx = memberList.selectedIndex;
            const one = memberList.selectedOptions.length === 1;
            document.getElementById("member-up").disabled = !one || idx === 0;
            document.getElementById("member-down").disabled = !one || idx === members.length - 1;
            document.getElementById("member-add").disabled = availableList.selectedOptions.length === 0;
            document.getElementById("member-remove").disabled = memberList.selectedOptions.length === 0;
        }

        function addSelected() {
            for (const o of [...availableList.selectedOptions]) members.push(o.value);
            renderMembers();
        }
        function removeSelected() {
            const drop = new Set([...memberList.selectedOptions].map((o) => o.value));
            members = members.filter((n) => !drop.has(n));
            renderMembers();
        }
        function moveSelected(delta) {
            const i = memberList.selectedIndex;
            const j = i + delta;
            if (i < 0 || j < 0 || j >= members.length) return;
            [members[i], members[j]] = [members[j], members[i]];
            renderMembers();
        }
        document.getElementById("member-add").addEventListener("click", addSelected);
        document.getElementById("member-remove").addEventListener("click", removeSelected);
        document.getElementById("member-up").addEventListener("click", () => moveSelected(-1));
        document.getElementById("member-down").addEventListener("click", () => moveSelected(1));
        availableList.addEventListener("dblclick", addSelected);
        memberList.addEventListener("dblclick", removeSelected);
        availableList.addEventListener("change", renderMembers);
        memberList.addEventListener("change", renderMembers);
        renderMembers();
```

The existing `wireConfigPage` call keeps `buildDto` (sends `members`), `fillForm` (`members = [...dto.members]; renderMembers();`) and `resetForm` (`members = []; renderMembers();`) as they are.

- [ ] **Step 3: CSS**

Append to `site.css` after the dialog rules:

```css
/* Pool member editor: available | buttons | members. Stacks on phones with
   the buttons as a row between the lists. */
.cw-duallist-grid { display: grid; grid-template-columns: 1fr auto 1fr; gap: 12px; align-items: start; }
.cw-duallist-grid label { margin-bottom: 0; }
.cw-duallist-buttons { display: flex; flex-direction: column; gap: 6px; padding-top: 1.9rem; }
.cw-duallist-buttons button { margin: 0; white-space: nowrap; }
.cw-duallist select[multiple] { font-family: var(--mono); font-size: 0.85rem; }
@media (max-width: 767px) {
    .cw-duallist-grid { grid-template-columns: 1fr; }
    .cw-duallist-buttons { flex-direction: row; flex-wrap: wrap; padding-top: 0; }
}
```

- [ ] **Step 4: Browser check, desktop**

Reload `/Config/Pools`, click Add pool. Available lists every target; Members is empty; Add, Remove, Up, Down are disabled. Select two in Available, Add: they move right in alphabetical order and disappear from the left. Select the second member, Up enables, click: order swaps. Double-click a member: it returns to Available. Fill a name, Save: the table row shows the members joined by arrows in the order shown.

- [ ] **Step 5: Browser check, Edit**

Edit the pool just saved: Members shows its order, Available the rest. Cancel.

- [ ] **Step 6: Browser check, phone width**

Set the viewport to the mobile preset, open the dialog: lists stacked, buttons in a row between them, no horizontal page scroll. Reset to desktop.

- [ ] **Step 7: Browser check, Review Focus 5**

Edit a pool, reorder a member, press Escape, click Add pool: Members is empty and every target is in Available. Then delete the test pool through the confirm dialog.

- [ ] **Step 8: Commit**

```bash
git add Programs/ConWatch
git commit -m "feat(web): two-list pool member editor with reorder"
```

---

### Task 8: Hop classification

**Files:**
- Modify: `Core/ConCore/ProbeSpec.cs:60-73`
- Create: `Libraries/ConInfrastructure/Traceroute/HopClassifier.cs`
- Modify: `Libraries/ConInfrastructure/Traceroute/TracerouteRunner.cs:7,49-55`
- Test: `Tests/ConEngine.Tests/HopClassifierTests.cs` (new), `Tests/ConEngine.Tests/ProbePolicyTests.cs` (new or existing — check `Tests/ConEngine.Tests` for a file testing `ProbePolicy`)

**Interfaces:**
- Produces: `ProbePolicy.IsPrivateOrCarrier(IPAddress)` and `(string)`; `HopKind` enum (`Internal`, `Path`, `Destination`); `TracerouteHop` gains `HopKind? Kind`, `int? Asn`, `string? AsName`; `HopClassifier.Classify(IReadOnlyList<TracerouteHop>) → IReadOnlyList<TracerouteHop>`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/HopClassifierTests.cs`:

```csharp
using ConInfrastructure.Traceroute;

namespace ConEngine.Tests;

public class HopClassifierTests
{
    private static TracerouteHop Hop(int ttl, string? address, bool reached = false) =>
        new(ttl, address, null, address is null ? null : ttl * 10, reached, null, null, null);

    private static IReadOnlyList<HopKind?> Kinds(params TracerouteHop[] hops) =>
        HopClassifier.Classify(hops).Select(h => h.Kind).ToList();

    [Fact]
    public void Lan_then_public_then_destination()
    {
        Assert.Equal([HopKind.Internal, HopKind.Internal, HopKind.Path, HopKind.Path, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, "192.168.100.1"), Hop(3, "96.120.0.1"),
                  Hop(4, "68.86.0.1"), Hop(5, "8.8.8.8", reached: true)));
    }

    [Fact]
    public void Cgnat_before_the_first_public_hop_is_internal()
    {
        Assert.Equal([HopKind.Internal, HopKind.Internal, HopKind.Path, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, "100.64.0.1"), Hop(3, "96.120.0.1"),
                  Hop(4, "1.1.1.1", reached: true)));
    }

    [Fact]
    public void Private_hop_after_a_public_one_is_path()
    {
        Assert.Equal([HopKind.Internal, HopKind.Path, HopKind.Path, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, "96.120.0.1"), Hop(3, "10.255.0.1"),
                  Hop(4, "1.1.1.1", reached: true)));
    }

    [Fact]
    public void Unanswered_hops_have_no_kind()
    {
        Assert.Equal([HopKind.Internal, null, HopKind.Path, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, null), Hop(3, "96.120.0.1"), Hop(4, "8.8.8.8", reached: true)));
    }

    [Fact]
    public void Private_destination_is_destination()
    {
        // Tracing to a LAN host: never "Internal", so the profile suggestion is Public,
        // which the user then corrects; and never an ASN candidate (Task 10).
        Assert.Equal([HopKind.Internal, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, "192.168.1.50", reached: true)));
    }

    [Fact]
    public void Other_fields_pass_through_unchanged()
    {
        var input = new TracerouteHop(3, "96.120.0.1", "ae1.example.net", 12, false, null, 7922, "COMCAST-7922");
        var output = HopClassifier.Classify([input]).Single();
        Assert.Equal(input with { Kind = HopKind.Path }, output);
    }
}
```

Add to the same test project a `ProbePolicyTests.cs` (or extend the existing one) with:

```csharp
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("100.64.0.1", true)]     // CGNAT: carrier space, not the user's LAN but not public either
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("example.com", false)]
    public void IsPrivateOrCarrier_covers_rfc1918_linklocal_loopback_and_cgnat(string address, bool expected)
    {
        Assert.Equal(expected, ConCore.ProbePolicy.IsPrivateOrCarrier(address));
    }

    [Fact]
    public void IsPrivate_still_excludes_cgnat_so_the_tcp_port_policy_is_unchanged()
    {
        Assert.False(ConCore.ProbePolicy.IsPrivate("100.64.0.1"));
    }
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~HopClassifierTests|FullyQualifiedName~ProbePolicyTests"`
Expected: build errors.

- [ ] **Step 3: `IsPrivateOrCarrier`**

In `Core/ConCore/ProbeSpec.cs` after the `IsPrivate` method add:

```csharp
    /// <summary>
    /// <see cref="IsPrivate"/> plus carrier-grade NAT space (100.64.0.0/10):
    /// an address that is not routable on the public internet. Used to
    /// classify traceroute hops and to keep such addresses out of external
    /// lookups. Not used by the TCP port policy, which treats CGNAT as public.
    /// </summary>
    public static bool IsPrivateOrCarrier(string address) =>
        IPAddress.TryParse(address, out var ip) && IsPrivateOrCarrier(ip);

    public static bool IsPrivateOrCarrier(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (IsPrivate(ip.ToString()))
            return true;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] is >= 64 and <= 127;
    }
```

- [ ] **Step 4: Hop record and classifier**

In `Libraries/ConInfrastructure/Traceroute/TracerouteRunner.cs` replace line 7 with:

```csharp
/// <summary>Where a hop sits: on the user's LAN, on the route beyond it, or the destination itself.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HopKind>))]
public enum HopKind { Internal, Path, Destination }

/// <summary>One hop in a traceroute result. Kind is null for a hop that did not answer;
/// Asn/AsName are filled only when the operator lookup is on and succeeded.</summary>
public record TracerouteHop(int Ttl, string? Address, string? Hostname, long? RttMs, bool ReachedDestination,
    HopKind? Kind, int? Asn, string? AsName);
```

And change the `hops.Add(new TracerouteHop(...))` call (lines 49-51) to pass `null, null, null` for the three new fields; change `return hops;` (line 55) to `return HopClassifier.Classify(hops);`.

Create `Libraries/ConInfrastructure/Traceroute/HopClassifier.cs`:

```csharp
using ConCore;

namespace ConInfrastructure.Traceroute;

/// <summary>
/// Labels each answering hop. Private, link-local, loopback and CGNAT
/// addresses BEFORE the first public hop are the user's side (Internal);
/// everything else before the destination is on the route (Path), including
/// a private address that shows up inside a carrier; the hop that reached the
/// destination is Destination whatever its address.
/// </summary>
public static class HopClassifier
{
    public static IReadOnlyList<TracerouteHop> Classify(IReadOnlyList<TracerouteHop> hops)
    {
        var seenPublic = false;
        var result = new List<TracerouteHop>(hops.Count);
        foreach (var hop in hops)
        {
            if (hop.Address is null)
            {
                result.Add(hop with { Kind = null });
                continue;
            }
            HopKind kind;
            if (hop.ReachedDestination)
                kind = HopKind.Destination;
            else if (!seenPublic && ProbePolicy.IsPrivateOrCarrier(hop.Address))
                kind = HopKind.Internal;
            else
                kind = HopKind.Path;
            if (!ProbePolicy.IsPrivateOrCarrier(hop.Address))
                seenPublic = true;
            result.Add(hop with { Kind = kind });
        }
        return result;
    }
}
```

- [ ] **Step 5: Build and run the tests**

Run: `dotnet build ConnectionWatcher.sln; dotnet test Tests/ConEngine.Tests`
Expected: `TracerouteRunnerTests` still pass (they construct hops through the runner, not the record); the new tests pass. If `TracerouteApiTests` in `ConWatch.Tests` compares the JSON shape, run it too: `dotnet test Tests/ConWatch.Tests --filter TracerouteApiTests`.

- [ ] **Step 6: Commit**

```bash
git add Core Libraries/ConInfrastructure Tests/ConEngine.Tests
git commit -m "feat(traceroute): classify hops as Internal, Path, or Destination"
```

---

### Task 9: ASN lookup service (Team Cymru over DNS)

**Files:**
- Modify: `Libraries/ConInfrastructure/ConInfrastructure.csproj`
- Create: `Libraries/ConInfrastructure/Asn/IAsnLookup.cs`
- Create: `Libraries/ConInfrastructure/Asn/CymruResponseParser.cs`
- Create: `Libraries/ConInfrastructure/Asn/CymruAsnLookup.cs`
- Create: `Libraries/ConInfrastructure/Asn/NullAsnLookup.cs`
- Test: `Tests/ConEngine.Tests/CymruResponseParserTests.cs` (new)

**Interfaces:**
- Produces: `AsnInfo(int Asn, string Name, string? CountryCode)`; `IAsnLookup.LookupAsync(IPAddress, CancellationToken) → Task<AsnInfo?>`; `CymruResponseParser.ParseOrigin(string?) → int?`; `CymruResponseParser.ParseAsName(string?) → (string Name, string? CountryCode)?`; `CymruResponseParser.OriginQueryName(IPAddress) → string`; `CymruResponseParser.AsQueryName(int) → string`; `NullAsnLookup.Instance`; `CymruAsnLookup` (DI-registered).

- [ ] **Step 1: Write the failing parser tests**

Create `Tests/ConEngine.Tests/CymruResponseParserTests.cs`:

```csharp
using System.Net;
using ConInfrastructure.Asn;

namespace ConEngine.Tests;

public class CymruResponseParserTests
{
    [Fact]
    public void Origin_query_name_reverses_the_octets()
    {
        Assert.Equal("4.3.2.1.origin.asn.cymru.com", CymruResponseParser.OriginQueryName(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void As_query_name()
    {
        Assert.Equal("AS7922.asn.cymru.com", CymruResponseParser.AsQueryName(7922));
    }

    [Fact]
    public void Origin_answer_yields_the_asn()
    {
        Assert.Equal(7922, CymruResponseParser.ParseOrigin("7922 | 73.0.0.0/8 | US | arin | 2005-01-27"));
    }

    [Fact]
    public void Origin_answer_with_two_asns_takes_the_first()
    {
        Assert.Equal(7922, CymruResponseParser.ParseOrigin("7922 33667 | 73.0.0.0/8 | US | arin | 2005-01-27"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("NA | 10.0.0.0/8 | | | ")]
    public void Unparseable_origin_is_null(string? text)
    {
        Assert.Null(CymruResponseParser.ParseOrigin(text));
    }

    [Fact]
    public void As_answer_yields_name_without_country_suffix()
    {
        var parsed = CymruResponseParser.ParseAsName("7922 | US | arin | 2005-01-27 | COMCAST-7922, US");
        Assert.Equal(("COMCAST-7922", "US"), parsed);
    }

    [Fact]
    public void As_answer_without_country_keeps_the_whole_name()
    {
        var parsed = CymruResponseParser.ParseAsName("64496 | | | | EXAMPLE-NET");
        Assert.Equal(("EXAMPLE-NET", null), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("7922 | US")]
    [InlineData("7922 | US | arin | 2005-01-27 |   ")]
    public void Unparseable_as_answer_is_null(string? text)
    {
        Assert.Null(CymruResponseParser.ParseAsName(text));
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~CymruResponseParserTests"`
Expected: build errors.

- [ ] **Step 3: Package reference**

In `Libraries/ConInfrastructure/ConInfrastructure.csproj` add inside the existing `PackageReference` ItemGroup:

```xml
    <PackageReference Include="DnsClient" Version="1.8.0" />
```

Run `dotnet restore Libraries/ConInfrastructure` and confirm it resolves.

- [ ] **Step 4: Interface, parser, null implementation**

Create `Libraries/ConInfrastructure/Asn/IAsnLookup.cs`:

```csharp
using System.Net;

namespace ConInfrastructure.Asn;

/// <summary>The autonomous system an address belongs to: number, registry name, country.</summary>
public record AsnInfo(int Asn, string Name, string? CountryCode);

/// <summary>
/// IP → autonomous system. Implementations return null rather than throw:
/// for a private/carrier address, a resolver that does not answer in time,
/// or an answer that does not parse. Results may be cached.
/// </summary>
public interface IAsnLookup
{
    Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct);
}
```

Create `Libraries/ConInfrastructure/Asn/NullAsnLookup.cs`:

```csharp
using System.Net;

namespace ConInfrastructure.Asn;

/// <summary>Never looks anything up; for tests and for the lookup-off path.</summary>
public sealed class NullAsnLookup : IAsnLookup
{
    public static NullAsnLookup Instance { get; } = new();
    public Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct) => Task.FromResult<AsnInfo?>(null);
}
```

Create `Libraries/ConInfrastructure/Asn/CymruResponseParser.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;

namespace ConInfrastructure.Asn;

/// <summary>
/// Pure parsing for Team Cymru's IP-to-ASN DNS service
/// (https://www.team-cymru.com/ip-asn-mapping). Two TXT lookups:
///   4.3.2.1.origin.asn.cymru.com → "7922 | 73.0.0.0/8 | US | arin | 2005-01-27"
///   AS7922.asn.cymru.com         → "7922 | US | arin | 2005-01-27 | COMCAST-7922, US"
/// </summary>
public static partial class CymruResponseParser
{
    public static string OriginQueryName(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.origin.asn.cymru.com";
    }

    public static string AsQueryName(int asn) => $"AS{asn}.asn.cymru.com";

    /// <summary>The first ASN of the origin answer; null when there is none.</summary>
    public static int? ParseOrigin(string? text)
    {
        var first = Fields(text).FirstOrDefault();
        if (first is null)
            return null;
        var token = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(token, out var asn) && asn > 0 ? asn : null;
    }

    /// <summary>The AS name with its trailing ", CC" country removed, plus that country.</summary>
    public static (string Name, string? CountryCode)? ParseAsName(string? text)
    {
        var fields = Fields(text);
        if (fields.Count < 5 || fields[4].Length == 0)
            return null;
        var match = CountrySuffix().Match(fields[4]);
        return match.Success
            ? (fields[4][..match.Index].TrimEnd(), match.Groups[1].Value)
            : (fields[4], null);
    }

    private static List<string> Fields(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split('|').Select(f => f.Trim()).ToList();

    [GeneratedRegex(@",\s*([A-Z]{2})$")]
    private static partial Regex CountrySuffix();
}
```

- [ ] **Step 5: The DNS implementation**

Create `Libraries/ConInfrastructure/Asn/CymruAsnLookup.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using ConCore;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;

namespace ConInfrastructure.Asn;

/// <summary>
/// IP → ASN through Team Cymru's DNS service, via the system resolver, with
/// a 2-second cap per query and a per-process cache (24 h for answers,
/// 10 min for misses). Private and carrier addresses are never queried.
/// </summary>
public sealed class CymruAsnLookup(ILogger<CymruAsnLookup> log, TimeProvider? timeProvider = null) : IAsnLookup
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<IPAddress, (AsnInfo? Info, DateTimeOffset Expires)> _cache = new();
    private readonly LookupClient _dns = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(2),
        Retries = 0,
        UseCache = true,
    });

    public async Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct)
    {
        if (ProbePolicy.IsPrivateOrCarrier(address))
            return null;
        if (_cache.TryGetValue(address, out var cached) && cached.Expires > _time.GetUtcNow())
            return cached.Info;

        AsnInfo? info = null;
        try
        {
            var asn = CymruResponseParser.ParseOrigin(
                await TxtAsync(CymruResponseParser.OriginQueryName(address), ct).ConfigureAwait(false));
            if (asn is { } number)
            {
                var named = CymruResponseParser.ParseAsName(
                    await TxtAsync(CymruResponseParser.AsQueryName(number), ct).ConfigureAwait(false));
                info = new AsnInfo(number, named?.Name ?? $"AS{number}", named?.CountryCode);
            }
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException or TimeoutException)
        {
            log.LogDebug(ex, "ASN lookup for {Address} failed", address);
        }

        _cache[address] = (info, _time.GetUtcNow() + (info is null ? MissTtl : HitTtl));
        return info;
    }

    private async Task<string?> TxtAsync(string name, CancellationToken ct)
    {
        var result = await _dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct).ConfigureAwait(false);
        var record = result.Answers.TxtRecords().FirstOrDefault();
        return record is null ? null : string.Concat(record.Text);
    }
}
```

If `DnsResponseException` lives in a different namespace in DnsClient 1.8.0, follow the compiler (it is `DnsClient.DnsResponseException`).

- [ ] **Step 6: Build and run the parser tests**

Run: `dotnet build Libraries/ConInfrastructure; dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~CymruResponseParserTests"`
Expected: pass.

- [ ] **Step 7: Commit**

```bash
git add Libraries/ConInfrastructure Tests/ConEngine.Tests/CymruResponseParserTests.cs
git commit -m "feat(traceroute): IAsnLookup with a Team Cymru DNS implementation and parser"
```

---

### Task 10: Lookup wired into the runner, the setting, the endpoint, DI

**Files:**
- Modify: `Libraries/ConInfrastructure/Traceroute/TracerouteRunner.cs`
- Modify: `Libraries/ConPersistence/Settings/SettingsStore.cs:30` (new key)
- Modify: `Programs/ConWatch/Api/SettingsEndpoints.cs:70-76`
- Modify: `Programs/ConWatch/Api/TracerouteEndpoints.cs`
- Modify: `Programs/ConWatch/Startup/ConWatchServices.cs:52-53`
- Modify: `Programs/ConWatch/Pages/Settings.cshtml` (new Traceroute section)
- Test: `Tests/ConEngine.Tests/TracerouteRunnerTests.cs`, `Tests/ConWatch.Tests/SettingsApiTests.cs`, `Tests/ConWatch.Tests/TracerouteApiTests.cs`, `Tests/ConWatch.Tests/PageSmokeTests.cs`

**Interfaces:**
- Consumes: `IAsnLookup`, `NullAsnLookup.Instance`, `CymruAsnLookup` (Task 9); `HopKind`, `TracerouteHop` (Task 8).
- Produces: `TracerouteRunner(ITtlProbe, IAsnLookup, ILogger<TracerouteRunner>)`; `RunAsync(string host, bool lookupOperators, CancellationToken ct)` plus the old two-argument overload; `SettingKeys.TracerouteAsnLookup = "Traceroute.AsnLookup"`; `POST /api/v1/traceroute` response `{ host, asnLookupEnabled, hops[] }` with `kind`, `asn`, `asName` on each hop.

- [ ] **Step 1: Runner tests**

In `Tests/ConEngine.Tests/TracerouteRunnerTests.cs` change every `new TracerouteRunner(new FakeProbe(...), NullLogger<TracerouteRunner>.Instance)` to `new TracerouteRunner(new FakeProbe(...), NullAsnLookup.Instance, NullLogger<TracerouteRunner>.Instance)` (add `using ConInfrastructure.Asn;`). Then add:

```csharp
    private sealed class FakeAsn : IAsnLookup
    {
        public List<string> Asked { get; } = [];
        public Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct)
        {
            Asked.Add(address.ToString());
            return Task.FromResult<AsnInfo?>(address.ToString() == "198.51.100.11"
                ? new AsnInfo(64496, "EXAMPLE-ISP", "US") : null);
        }
    }

    [Fact]
    public async Task Lookup_fills_asn_on_path_and_destination_hops_only()
    {
        var asn = new FakeAsn();
        var runner = new TracerouteRunner(new FakeProbe("10.0.0.1", "198.51.100.11", "8.8.8.8"), asn,
            NullLogger<TracerouteRunner>.Instance);
        var hops = await runner.RunAsync("8.8.8.8", lookupOperators: true, CancellationToken.None);

        Assert.Equal(["198.51.100.11", "8.8.8.8"], asn.Asked.OrderBy(a => a));
        Assert.Null(hops[0].Asn);
        Assert.Equal(64496, hops[1].Asn);
        Assert.Equal("EXAMPLE-ISP", hops[1].AsName);
        Assert.Null(hops[2].Asn); // the fake answers null for 8.8.8.8: a miss leaves the fields empty
    }

    [Fact]
    public async Task Lookup_skips_private_and_unanswered_hops()
    {
        var asn = new FakeAsn();
        var runner = new TracerouteRunner(new FakeProbe("192.168.1.1", null, "192.168.1.50"), asn,
            NullLogger<TracerouteRunner>.Instance);
        var hops = await runner.RunAsync("192.168.1.50", lookupOperators: true, CancellationToken.None);

        Assert.Equal(HopKind.Destination, hops[2].Kind);
        Assert.Empty(asn.Asked); // Review Focus 1: a private destination is never sent out
    }

    [Fact]
    public async Task Lookup_off_asks_nothing()
    {
        var asn = new FakeAsn();
        var runner = new TracerouteRunner(new FakeProbe("10.0.0.1", "198.51.100.11", "8.8.8.8"), asn,
            NullLogger<TracerouteRunner>.Instance);
        await runner.RunAsync("8.8.8.8", lookupOperators: false, CancellationToken.None);
        Assert.Empty(asn.Asked);
    }
```

- [ ] **Step 2: API tests**

In `Tests/ConWatch.Tests/SettingsApiTests.cs` add:

```csharp
    [Fact]
    public async Task Asn_lookup_setting_accepts_only_booleans()
    {
        var bad = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Traceroute.AsnLookup"] = "yes" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Traceroute.AsnLookup"] = "true" });
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
        Assert.Equal("true", (await GetSettings())["Traceroute.AsnLookup"]);
    }
```

In `Tests/ConWatch.Tests/TracerouteApiTests.cs` extend `Traceroute_returns_ordered_hops` with, after the existing asserts:

```csharp
        Assert.False(body.GetProperty("asnLookupEnabled").GetBoolean());
        Assert.Equal("Internal", hops[0].GetProperty("kind").GetString());
        Assert.Equal("Path", hops[1].GetProperty("kind").GetString());
        Assert.Equal("Destination", hops[2].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, hops[1].GetProperty("asn").ValueKind);
```

and add a fake lookup beside `FakeProbe` (add `using ConInfrastructure.Asn;`):

```csharp
    /// <summary>Knows one address; everything else is a miss.</summary>
    private sealed class FakeAsn : IAsnLookup
    {
        public Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct) =>
            Task.FromResult<AsnInfo?>(address.ToString() == "198.51.100.11"
                ? new AsnInfo(64496, "EXAMPLE-ISP", "US") : null);
    }
```

registered in `Factory.ConfigureWebHost` next to the probe:

```csharp
                services.RemoveAll<IAsnLookup>();
                services.AddSingleton<IAsnLookup, FakeAsn>();
```

then this test:

```csharp
    [Fact]
    public async Task Operator_column_appears_when_the_setting_is_on()
    {
        var client = _factory.CreateClient();
        await client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Traceroute.AsnLookup"] = "true" });
        var response = await client.PostAsJsonAsync("/api/v1/traceroute", new { host = "8.8.8.8" });
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.True(body.GetProperty("asnLookupEnabled").GetBoolean());
        var hops = body.GetProperty("hops").EnumerateArray().ToList();
        Assert.Equal("EXAMPLE-ISP", hops[1].GetProperty("asName").GetString());
        Assert.Equal(64496, hops[1].GetProperty("asn").GetInt32());
    }
```

In `PageSmokeTests.Page_renders` add `[InlineData("/Settings", "Look up the operator of each hop")]`.

- [ ] **Step 3: Run to see them fail**

Run: `dotnet test Tests/ConEngine.Tests --filter TracerouteRunnerTests; dotnet test Tests/ConWatch.Tests --filter "SettingsApiTests|TracerouteApiTests|PageSmokeTests"`
Expected: build errors in the runner tests; 400 "Unknown setting"; missing JSON properties.

- [ ] **Step 4: Runner**

Replace `Libraries/ConInfrastructure/Traceroute/TracerouteRunner.cs` from the class declaration down with:

```csharp
public class TracerouteRunner(ITtlProbe probe, IAsnLookup asnLookup, ILogger<TracerouteRunner> log)
{
    private const int MaxTtl = 30;
    private const int ProbesPerHop = 3;
    private const int ProbeTimeoutMs = 2000;
    private const int LookupParallelism = 8;

    public Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, CancellationToken ct) =>
        RunAsync(host, lookupOperators: false, ct);

    /// <param name="lookupOperators">When true, every Path/Destination hop with a
    /// public address is sent to <see cref="IAsnLookup"/> after the walk.</param>
    public async Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, bool lookupOperators, CancellationToken ct)
    {
        IPAddress destination;
        if (!IPAddress.TryParse(host, out destination!))
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            destination = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?? throw new ArgumentException($"No IPv4 address for '{host}'.");
        }
        log.LogInformation("Traceroute to {Host} ({Destination})", host, destination);

        var hops = new List<TracerouteHop>();
        for (int ttl = 1; ttl <= MaxTtl; ttl++)
        {
            IPAddress? hopAddress = null;
            long? bestRtt = null;
            bool reached = false;

            for (int i = 0; i < ProbesPerHop; i++)
            {
                var result = await probe.ProbeAsync(destination, ttl, ProbeTimeoutMs, ct).ConfigureAwait(false);
                if (result.Hop is not null)
                {
                    hopAddress ??= result.Hop;
                    bestRtt = bestRtt is { } rtt ? Math.Min(rtt, result.RttMs) : result.RttMs;
                }
                reached |= result.ReachedDestination;
            }

            hops.Add(new TracerouteHop(ttl, hopAddress?.ToString(),
                hopAddress is null ? null : await ReverseDnsAsync(hopAddress, ct).ConfigureAwait(false),
                bestRtt, reached, null, null, null));
            if (reached)
                break;
        }

        var classified = HopClassifier.Classify(hops);
        return lookupOperators ? await WithOperatorsAsync(classified, ct).ConfigureAwait(false) : classified;
    }

    /// <summary>Fills Asn/AsName on Path and Destination hops with public addresses, a few at a time.</summary>
    private async Task<IReadOnlyList<TracerouteHop>> WithOperatorsAsync(IReadOnlyList<TracerouteHop> hops, CancellationToken ct)
    {
        var result = hops.ToArray();
        using var gate = new SemaphoreSlim(LookupParallelism);
        var tasks = result.Select(async (hop, i) =>
        {
            if (hop.Kind is not (HopKind.Path or HopKind.Destination) ||
                !IPAddress.TryParse(hop.Address, out var ip) || ConCore.ProbePolicy.IsPrivateOrCarrier(ip))
                return;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await asnLookup.LookupAsync(ip, ct).ConfigureAwait(false) is { } info)
                    result[i] = hop with { Asn = info.Asn, AsName = info.Name };
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    private static async Task<string?> ReverseDnsAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(address.ToString(), ct);
            var winner = await Task.WhenAny(lookup, Task.Delay(1000, ct)).ConfigureAwait(false);
            return winner == lookup ? (await lookup.ConfigureAwait(false)).HostName : null;
        }
        catch
        {
            return null;
        }
    }
}
```

Add `using ConInfrastructure.Asn;` at the top. (The `HopKind` and `TracerouteHop` declarations from Task 8 stay above the class.)

- [ ] **Step 5: Setting key and validation**

`Libraries/ConPersistence/Settings/SettingsStore.cs` after line 30 (`HeartbeatIntervalMinutes`):

```csharp
    /// <summary>"true" lets the traceroute page send each public hop's address
    /// to Team Cymru's IP-to-ASN DNS service to name its operator. Off by default.</summary>
    public const string TracerouteAsnLookup = "Traceroute.AsnLookup";
```

`Programs/ConWatch/Api/SettingsEndpoints.cs`: add `case SettingKeys.TracerouteAsnLookup:` to the bool group at lines 70-73.

- [ ] **Step 6: Endpoint and DI**

`Programs/ConWatch/Api/TracerouteEndpoints.cs`:

```csharp
using ConInfrastructure.Traceroute;
using ConPersistence.Settings;

namespace ConWatch.Api;

public static class TracerouteEndpoints
{
    public record TracerouteRequest(string Host);

    public static void MapTraceroute(this RouteGroupBuilder api)
    {
        api.MapPost("/traceroute", async (TracerouteRequest request, TracerouteRunner runner,
            SettingsStore settings, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Host))
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "'host' is required.");
            var lookup = await settings.GetAsync(SettingKeys.TracerouteAsnLookup) == "true";
            try
            {
                var hops = await runner.RunAsync(request.Host.Trim(), lookup, ct);
                return Results.Ok(new { Host = request.Host.Trim(), AsnLookupEnabled = lookup, Hops = hops });
            }
            catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, $"Cannot resolve '{request.Host}': {ex.Message}");
            }
        });
    }
}
```

`Programs/ConWatch/Startup/ConWatchServices.cs`: before the `TracerouteRunner` registration (line 53) add:

```csharp
        services.AddSingleton<ConInfrastructure.Asn.IAsnLookup, ConInfrastructure.Asn.CymruAsnLookup>();
```

- [ ] **Step 7: Settings page section**

In `Programs/ConWatch/Pages/Settings.cshtml`, before `<h3>Notifications</h3>` insert:

```cshtml
<h3>Traceroute</h3>
<ul class="form-errors" id="traceroute-errors"></ul>
<form id="traceroute-form">
    <label>Look up the operator of each hop
        <select name="@SettingKeys.TracerouteAsnLookup">
            <option value="false" selected="@(Model.Setting(SettingKeys.TracerouteAsnLookup, "false") != "true")">off</option>
            <option value="true" selected="@(Model.Setting(SettingKeys.TracerouteAsnLookup) == "true")">on</option>
        </select>
    </label>
    <p><small>When on, the Traceroute page sends the address of every public hop to
    Team Cymru's IP-to-ASN service through your DNS resolver, and names the operator
    ("COMCAST-7922") in the table and in "Add as target". Private addresses are never sent.</small></p>
    <button type="submit">Save traceroute settings</button>
</form>
```

In `site-settings.js` add `wireSettingsForm("traceroute-form", "traceroute-errors");` beside the `notify-form` line.

- [ ] **Step 8: Run everything**

Run: `dotnet test ConnectionWatcher.sln`
Expected: all pass. The CLI project does not construct `TracerouteRunner`; if the build says otherwise, pass `NullAsnLookup.Instance` there.

- [ ] **Step 9: Commit**

```bash
git add Libraries Programs/ConWatch Tests
git commit -m "feat(traceroute): optional operator lookup (Traceroute.AsnLookup) wired through runner, API, and Settings"
```

---

### Task 11: Traceroute page columns and prefilled "Add as target"

**Files:**
- Modify: `Programs/ConWatch/Pages/Traceroute.cshtml:26-29`
- Modify: `Programs/ConWatch/wwwroot/lib/site-traceroute.js:34-63`
- Modify: `Programs/ConWatch/Pages/Config/Targets.cshtml.cs:26-29`
- Modify: `Programs/ConWatch/Pages/Config/Targets.cshtml` (Profile select, operator input)
- Test: `Tests/ConWatch.Tests/PageSmokeTests.cs`

- [ ] **Step 1: Smoke test for the prefill**

In `PageSmokeTests` add:

```csharp
    [Fact]
    public async Task Targets_page_prefills_profile_and_operator_from_the_query()
    {
        var html = await _factory.CreateClient().GetStringAsync(
            "/Config/Targets?address=96.120.0.1&name=hop-3&profile=Path&operator=COMCAST-7922");
        Assert.Contains("value=\"COMCAST-7922\"", html);
        Assert.Matches("<option selected=\"selected\"[^>]*>Path</option>", html);
        Assert.DoesNotMatch("<option selected=\"selected\"[^>]*>Public</option>", html);
    }

    [Fact]
    public async Task Unknown_prefilled_profile_falls_back_to_Public()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets?address=1.2.3.4&profile=Nope");
        Assert.Matches("<option selected=\"selected\"[^>]*>Public</option>", html);
    }
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test Tests/ConWatch.Tests --filter PageSmokeTests`
Expected: the Path assertion fails (Public is selected).

- [ ] **Step 3: Targets prefill**

`Programs/ConWatch/Pages/Config/Targets.cshtml.cs`: after `PrefillParent` add:

```csharp
    public string? PrefillProfile => Request.Query["profile"];
    public string? PrefillOperator => Request.Query["operator"];

    /// <summary>The profile the dialog selects: the prefilled one when it exists, else Public.</summary>
    public string DefaultProfile =>
        Config.Profiles.Any(p => p.Name.Equals(PrefillProfile, StringComparison.OrdinalIgnoreCase))
            ? PrefillProfile! : "Public";
```

`Programs/ConWatch/Pages/Config/Targets.cshtml`: the Profile option becomes
`<option selected="@p.Name.Equals(Model.DefaultProfile, StringComparison.OrdinalIgnoreCase)" title="@p.Description">@p.Name</option>`
and the operator input becomes
`<input name="operator" placeholder="e.g. Comcast" value="@Model.PrefillOperator" />`.

- [ ] **Step 4: Traceroute table and links**

`Programs/ConWatch/Pages/Traceroute.cshtml` lines 26-29:

```cshtml
<table id="hops">
    <thead><tr><th>TTL</th><th>Address</th><th>Hostname</th><th>RTT</th><th>Kind</th><th id="operator-head" hidden>Operator</th><th></th></tr></thead>
    <tbody></tbody>
</table>
```

In `site-traceroute.js` replace lines 34-63 (the hop loop) with:

```javascript
        const profileFor = { Internal: "Internal", Path: "Path", Destination: "Public" };
        document.getElementById("operator-head").hidden = !body.asnLookupEnabled;

        let nearestKnownParent = null;
        for (const hop of body.hops) {
            const known = hop.address ? byAddress.get(hop.address) : undefined;
            const row = document.createElement("tr");

            const addCell = document.createElement("td");
            if (known) {
                addCell.textContent = `already monitored as '${known.name}'`;
            } else if (hop.address) {
                const link = document.createElement("a");
                link.setAttribute("role", "button");
                link.className = "outline";
                const name = hop.reachedDestination ? host : `hop-${hop.ttl}`;
                const params = new URLSearchParams({ address: hop.address, name });
                if (nearestKnownParent) params.set("parent", nearestKnownParent);
                if (hop.kind) params.set("profile", profileFor[hop.kind]);
                if (hop.asName) params.set("operator", hop.asName);
                link.href = `/Config/Targets?${params}`;
                link.textContent = "Add as target";
                addCell.appendChild(link);
            }

            const cells = [
                hop.ttl,
                hop.address ?? "*",
                hop.hostname ?? "",
                hop.rttMs != null ? hop.rttMs + " ms" : "—",
                hop.kind ?? "",
            ];
            if (body.asnLookupEnabled) cells.push(hop.asName ?? "");
            for (const text of cells) {
                const td = document.createElement("td");
                td.textContent = String(text);
                row.appendChild(td);
            }
            row.appendChild(addCell);
            tbody.appendChild(row);

            if (known) nearestKnownParent = known.name;
        }
```

(Cells are built with `textContent` now, so a hostname can no longer inject markup.)

- [ ] **Step 5: Run the web tests**

Run: `dotnet test Tests/ConWatch.Tests`
Expected: all pass.

- [ ] **Step 6: Browser check**

With the lookup off: run a traceroute to 8.8.8.8 in the preview. The Kind column reads Internal for the LAN hops, Path beyond, Destination on the last row; no Operator column. Click "Add as target" on a Path hop: the Targets dialog opens with Profile = Path and the parent suggestion as before. Cancel.

Turn the lookup on under Settings → Traceroute, run again: the Operator column appears and names the ISP on public hops (depends on the real resolver; blank cells are acceptable for hops Cymru does not know). "Add as target" on such a hop fills the operator input. Turn the lookup back off. Check the console for errors.

- [ ] **Step 7: Commit**

```bash
git add Programs/ConWatch Tests/ConWatch.Tests
git commit -m "feat(traceroute): Kind and Operator columns; Add as target pre-selects profile and operator"
```

---

### Task 12: README, backlog, full verification

**Files:**
- Modify: `README.md` (see the rules below)
- Modify: `docs/superpowers/BACKLOG.md`

**README rules.** Mark is mid-rewrite and the file is uncommitted. Edit ONLY the lines below; do not touch the intro, the First run section, the Web UI bullets for Dashboard / History / Data, or the `# Scraps` section. Do not stage or commit README.md; Task 12's commit is the backlog only. Use `grep -n` to find the lines since numbers shift with Mark's edits.

- [ ] **Step 1: README edits**

1. The bullet beginning `- **Targets** (\`/Config/Targets\`, \`/Config/Profiles\`, \`/Config/Pools\`) — CRUD` becomes, in Mark's bullet style:

```markdown
- **Config** (`/Config/Targets`, `/Config/Pools`, `/Config/Profiles`)

  - Targets: the path to the internet and the sites watched beyond it. Each has a
    profile, an optional parent, and an optional **ISP/operator name** ("Comcast"):
    link verdicts then say "Comcast is down" rather than naming a hop, and the
    verdict counts anchors by operator, so four addresses of one provider failing
    together stay a site incident, never internet weather.
  - Pools: rotation groups that share the pinging load; the one chosen as the
    **verdict anchor** supplies the internet anchors the verdict reads.
  - Profiles: ping cadence bundles. Three are built in — **Internal** for your own
    LAN, **Path** for hops to the internet you don't own, **Public** for sites
    where you should be polite — and each carries a description you can edit.
  - Whole-config export and import.
```

   Keep whatever sentences from the old bullet that are not covered above if they still read true; drop the "CRUD with inline validation errors" phrasing.

2. The `- **Traceroute** (\`/Traceroute\`)` bullet becomes:

```markdown
- **Traceroute** (`/Traceroute`)

  - Discover the hops between you and a destination, labelled Internal, Path, or
    Destination, and add any of them as a monitored target with parent and profile
    pre-filled.
  - Optionally (Settings → Traceroute) look up each public hop's operator through
    Team Cymru's IP-to-ASN service, so the operator name is pre-filled too.
```

3. Every remaining `Admin page` / `**Admin** page` outside `# Scraps` becomes `Settings page` (the Notifications paragraph, the Admin password paragraph, the take-over sentence, the Monitoring toggle sentence). The phrase "Admin password" stays.
4. The tray paragraph `menu: History, the ISP report, Admin, and **Settings** (server URL with` becomes `menu: History, the ISP report, Server settings, and **Tray settings** (server URL with`.
5. In the Backup / export paragraph (find `Export config` or `config/export`), add one sentence: `A config export is tied to the version that wrote it; export again after upgrading.`

- [ ] **Step 2: Backlog**

Append to `docs/superpowers/BACKLOG.md`:

```markdown
## Pools (noted 2026-10-01)

- **One stagger slot for a target in two pools** — `WatcherEngine.cs` builds
  `poolPosition[targetId]` by overwriting, so a target that belongs to two pools
  takes its offset from whichever pool is enumerated last. Either warn in
  `ConfigHealth` or give the target a slot per pool. Found while reading
  `PoolCoordinator` for the config-refinements spec.
```

- [ ] **Step 3: Full test run and a last preview pass**

Run: `dotnet test ConnectionWatcher.sln`
Expected: all pass.

Preview, one pass through: nav labels; `/Admin` redirect; Pools verdict section and member editor; Profiles descriptions; Targets dialog default profile; Traceroute columns with the lookup off; Settings page without the verdict form and with the Traceroute section. No console errors.

- [ ] **Step 4: Commit the backlog only**

```bash
git add docs/superpowers/BACKLOG.md
git commit -m "docs(backlog): stagger-slot collision for a target in two pools"
git status --short   # README.md must still show as modified and unstaged
```
