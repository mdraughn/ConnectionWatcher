# Discoveries Inbox Implementation Plan (Plan 2 of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A node-local **Discoveries inbox**: candidates (the router, the first hop beyond the LAN, later local devices) found by manual searches or posted by a scout, listed on a new Config page, and adopted into targets or dismissed/restored — plus the REST endpoints, the server-side router and network-edge searches, and a read-only MCP `list_discoveries` tool.

**Architecture:** One new table (`Discoveries`) behind a `DiscoveryStore`; adoption happens inside `ConfigService.UpsertTargetAsync`'s existing transaction so "target created" and "discovery adopted" commit together or not at all. A small scanner library (`ConInfrastructure/Discovery`) wraps the existing gateway discovery and traceroute runner; a `DiscoverySearchService` serializes searches (one at a time, 30 s cap) and upserts results. The Config page is server-rendered Razor with inline vanilla JS; "Adopt" opens the existing Targets dialog pre-filled (`?address=&name=&profile=&scope=&discoveryId=`).

**Tech Stack:** .NET 10 / ASP.NET Razor Pages / EF Core + SQLite; xunit 2.9.3; ModelContextProtocol 2.2.0.

**Spec:** `docs/superpowers/specs/2026-10-02-discoveries-and-local-scope-design.md` (§1 the Discovery record, §2 searches, §4 endpoints and MCP, §5 errors, §6 testing). Plans 1A/1B are merged on `master`: `TargetScope`, the built-in `Local` profile, the Targets dialog with its `?scope=Local` prefill, History/dashboard LAN views.

**Series:** 1A, 1B (done) → **2 (this plan)** → 3 (LAN scan: ARP + ping sweep on the server; tray scouting with DPAPI-stored admin password; `network_mode: host` docs). This plan builds the `POST /api/v1/discoveries` batch endpoint the tray will call, but not the tray side or the LAN scan.

## Global Constraints

- File-scoped namespaces; `Nullable` and `ImplicitUsings` enabled; no `TreatWarningsAsErrors`.
- New tests use RFC 5737 addresses (`203.0.113.x`, `198.51.100.x`, `192.0.2.x`) and `example.com`. Tests wait on conditions (`ConfigApiTests.Eventually`), never sleep-then-assert (CI runners have 2 cores).
- Every `DateTime` column is UTC and named `*Utc`; `ConWatchDbContext` restores `DateTimeKind.Utc` on read for every `DateTime` property automatically.
- Enums cross JSON as strings (`[JsonConverter(typeof(JsonStringEnumConverter<T>))]` on the enum). `JsonStringEnumConverter` also accepts numbers, so **every inbound enum must be checked with `Enum.IsDefined`** (plan 1A learned this the hard way).
- Discoveries are **node-local observations**: never exported, cloned or part of any snapshot (`SnapshotExporter`/`SnapshotImporter` enumerate tables explicitly — add nothing there).
- Spec §4: **all discovery endpoints are admin-gated (the `locked` group) and nothing about discoveries is on an open endpoint**. The only open surface is the read-only MCP tool (the spec rules it in: an MCP client on the LAN can already scan the LAN). `ApiSurfaceLockTests` probes every mapped route with the gate on and fails for any route that is neither on its open allowlist nor 401 — new locked routes pass it automatically; do not add them to the allowlist.
- Searches: one at a time per node (a second request gets 409), 30 s overall cap with cancellation; LAN-only/private-range rules for the LAN scan belong to plan 3.
- Config pages use inline vanilla JS and `wwwroot/lib/site-config.js` helpers (`apiFetch`, `showErrors`, `clearErrors`, `confirmDialog`) — no Vite, no new dependencies.
- Migration command: `dotnet tool restore` then `dotnet ef migrations add <Name> --project Libraries/ConPersistence` (project-only; never `--startup-project`). Do not hand-edit generated Designer/model-snapshot files; `MigrationsTests` fails on a model change without a migration.
- Test commands per project (never the whole solution): `dotnet test Tests/ConPersistence.Tests -c Debug`, `Tests/ConEngine.Tests` (also hosts the `ConInfrastructure` tests), `Tests/ConWatch.Tests` (and `ConConfig.Tests`, `ConWatchCli.Tests`, `ConNotify.Tests` in the final task); filter with `--filter "FullyQualifiedName~<Class>"`. `ConWatch.Tests` has one known cold-start flake in `ProbeFieldTests` (passes on rerun). Frontend is untouched by this plan.
- Commit after every task. **Never push** unless the user says so. Commit messages carry no attribution lines. Work on a feature branch in a git worktree (e.g. `discoveries-inbox`), not on `master`. Do not edit `README.md` (the user has an uncommitted rewrite).

## Review Focus

Failure modes the spec implies that no task's main tests would catch; each has a test in the owning task:

1. **Re-finding a dismissed address must not resurrect it**, and must still refresh `LastSeenUtc` and the evidence (Task 1, Task 4).
2. **Adoption is atomic**: if the new target fails validation *after* the row was saved (e.g. a duplicate address), the discovery must still be `New` — not half-adopted (Task 2, proves the rollback).
3. **An adopted discovery whose target was deleted** stays `Adopted` with no target name, and can be dismissed or adopted again — but a live adoption cannot be dismissed, restored or re-adopted (Task 1, Task 2, Task 4).
4. **A hostile/garbled batch** (hostnames, loopback, `0.0.0.0`, oversized text, > 512 items, undefined numeric enum values) is a 400 with messages, never a 500 and never a partial write (Task 1, Task 4).
5. **Two simultaneous searches** — the second must get a clean 409, and the first must still finish and store its results (Task 4).
6. **A search that finds nothing or times out** says so plainly (zero found / `search-timeout`), keeps whatever the store already had, and never leaves the gate stuck (Task 3, Task 4).

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Core/ConCore/Discovery/DiscoveryEnums.cs` (new) | `DiscoveryKind`, `DiscoveryMethod`, `DiscoveryStatus` | 1 |
| `Libraries/ConPersistence/Entities/DiscoveryRow.cs` (new), `ConWatchDbContext.cs`, `Migrations/*AddDiscoveries*` | Table and mapping | 1 |
| `Libraries/ConPersistence/Discoveries/DiscoveryStore.cs` (new) | Upsert, list, dismiss, restore, status-filter parsing | 1 |
| `Libraries/ConPersistence/Config/ConfigService.cs` | Adopt inside the target-create transaction | 2 |
| `Libraries/ConInfrastructure/Discovery/DiscoveryScanner.cs` (new), `ContainerHint.cs` (new), `Traceroute/TracerouteRunner.cs` | Router and network-edge searches; container warning; early-stop traceroute | 3 |
| `Programs/ConWatch/Discoveries/DiscoverySearchService.cs` (new), `Api/DiscoveryEndpoints.cs` (new), `Api/ConfigEndpoints.cs`, `ConWatchApp.cs`, `Startup/ConWatchServices.cs` | Serialized searches, REST surface, DI | 4 |
| `Programs/ConWatch/Pages/Config/Discoveries.cshtml(.cs)` (new), `_ConfigTabs.cshtml`, `Targets.cshtml(.cs)` | The Config page; adopt hand-off | 5 |
| `Programs/ConWatch/Mcp/ConWatchMcpTools.cs` | `list_discoveries` | 6 |

New test files: `Tests/ConPersistence.Tests/DiscoveryStoreTests.cs`, `Tests/ConPersistence.Tests/DiscoveryAdoptTests.cs`, `Tests/ConEngine.Tests/DiscoveryScannerTests.cs`, `Tests/ConWatch.Tests/DiscoveryApiTests.cs`, `Tests/ConWatch.Tests/DiscoverySearchServiceTests.cs`, `Tests/ConWatch.Tests/DiscoveriesPageTests.cs`. Existing tests extended where named.

---

### Task 1: The Discoveries table and `DiscoveryStore`

**Files:**
- Create: `Core/ConCore/Discovery/DiscoveryEnums.cs`, `Libraries/ConPersistence/Entities/DiscoveryRow.cs`, `Libraries/ConPersistence/Discoveries/DiscoveryStore.cs`
- Modify: `Libraries/ConPersistence/ConWatchDbContext.cs`
- Generate: `Libraries/ConPersistence/Migrations/<timestamp>_AddDiscoveries*.cs` + model snapshot
- Test: create `Tests/ConPersistence.Tests/DiscoveryStoreTests.cs`

**Interfaces:**
- Produces: `ConCore.Discovery.{DiscoveryKind {Gateway, NetworkEdge, LocalDevice}, DiscoveryMethod {DefaultRoute, CommonAddress, Traceroute, ArpTable, PingSweep}, DiscoveryStatus {New, Adopted, Dismissed}}`; `ConPersistence.Discoveries.{DiscoveryInput, UpsertResult, DiscoveryDto, DiscoveryStore, DiscoveryStatuses}` with:
  - `DiscoveryStore(IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider? timeProvider = null)`
  - `Task<UpsertResult> UpsertAsync(IReadOnlyList<DiscoveryInput> inputs)`; `static IReadOnlyList<string> Validate(IReadOnlyList<DiscoveryInput> inputs)`
  - `Task<List<DiscoveryDto>> ListAsync(DiscoveryStatus? status)` (null = all; newest `LastSeenUtc` first)
  - `Task<ConfigResult> DismissAsync(Guid id)`, `Task<ConfigResult> RestoreAsync(Guid id)`, `Task<int> RestoreAllDismissedAsync()`
  - `DiscoveryStatuses.TryParseFilter(string? text, out DiscoveryStatus? status)` — null/empty/"new" → New, "dismissed", "adopted", "all" → null; false for anything else.
- Table: `Discoveries(Id, Address, Hostname?, Kind, Method, Origin, Evidence?, FirstSeenUtc, LastSeenUtc, Status, AdoptedTargetId?)`, unique on `(Address, Kind)`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConPersistence.Tests/DiscoveryStoreTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails (does not compile)**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~DiscoveryStoreTests"`
Expected: build FAIL — the discovery types, `db.Discoveries` and `DiscoveryStore` are not defined.

- [ ] **Step 3: Enums and entity**

Create `Core/ConCore/Discovery/DiscoveryEnums.cs`:

```csharp
using System.Text.Json.Serialization;

namespace ConCore.Discovery;

/// <summary>What a discovery is. Serialized as a string everywhere.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryKind>))]
public enum DiscoveryKind
{
    /// <summary>The user's router (default gateway).</summary>
    Gateway,
    /// <summary>The first hop beyond the user's LAN: where the ISP's network begins.</summary>
    NetworkEdge,
    /// <summary>A device on the LAN (camera, NAS, access point, IoT).</summary>
    LocalDevice,
}

/// <summary>How a discovery was found. Extensible; the kind says what, the method says how.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryMethod>))]
public enum DiscoveryMethod
{
    DefaultRoute,
    CommonAddress,
    Traceroute,
    ArpTable,
    PingSweep,
}

[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryStatus>))]
public enum DiscoveryStatus
{
    New,
    Adopted,
    Dismissed,
}
```

Create `Libraries/ConPersistence/Entities/DiscoveryRow.cs`:

```csharp
using ConCore.Discovery;

namespace ConPersistence.Entities;

/// <summary>
/// A candidate found by a search: an address nobody is monitoring yet. Node-local
/// (never exported or cloned). Unique per (Address, Kind); a Dismissed row stays
/// Dismissed when found again.
/// </summary>
public class DiscoveryRow
{
    public Guid Id { get; set; }
    public string Address { get; set; } = "";
    public string? Hostname { get; set; }
    public DiscoveryKind Kind { get; set; }
    public DiscoveryMethod Method { get; set; }
    /// <summary>Who found it: "server" or "tray:&lt;machine name&gt;".</summary>
    public string Origin { get; set; } = "server";
    /// <summary>Short human-readable proof: RTT, hop number, MAC vendor.</summary>
    public string? Evidence { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DiscoveryStatus Status { get; set; } = DiscoveryStatus.New;
    /// <summary>Set when adopted; may point at a target that has since been deleted.</summary>
    public Guid? AdoptedTargetId { get; set; }
}
```

- [ ] **Step 4: Map it and generate the migration**

`ConWatchDbContext.cs` — add `using ConCore.Discovery;` (the file already has `using ConCore.Targets;`), the set, and the mapping:

```csharp
    public DbSet<DiscoveryRow> Discoveries => Set<DiscoveryRow>();
```

inside `OnModelCreating` before the `utc` converter block:

```csharp
        modelBuilder.Entity<DiscoveryRow>(e =>
        {
            e.HasIndex(d => new { d.Address, d.Kind }).IsUnique();
            e.HasIndex(d => d.Status);
            e.Property(d => d.Address).UseCollation("NOCASE");
            e.Property(d => d.Kind).HasConversion<string>().HasMaxLength(24);
            e.Property(d => d.Method).HasConversion<string>().HasMaxLength(24);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(24);
        });
```

Run:

```bash
dotnet tool restore
dotnet ef migrations add AddDiscoveries --project Libraries/ConPersistence
```

Open `*_AddDiscoveries.cs`: `Up` must be a `CreateTable("Discoveries", …)` with the unique index on `(Address, Kind)` and an index on `Status`; `Down` drops the table. Do not edit the generated Designer or model snapshot.

- [ ] **Step 5: The store**

Create `Libraries/ConPersistence/Discoveries/DiscoveryStore.cs`:

```csharp
using System.Net;
using ConCore.Discovery;
using ConPersistence.Config;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Discoveries;

/// <summary>One candidate as posted by a search or a scout.</summary>
public sealed record DiscoveryInput(string Address, string? Hostname, DiscoveryKind Kind,
    DiscoveryMethod Method, string Origin, string? Evidence);

public sealed record UpsertResult(bool Success, IReadOnlyList<string> Errors, int Created, int Updated);

/// <summary>The wire/UI shape. <see cref="AdoptedTargetName"/> is null when the
/// adopted target no longer exists ("(target removed)").</summary>
public sealed record DiscoveryDto(Guid Id, string Address, string? Hostname, DiscoveryKind Kind,
    DiscoveryMethod Method, string Origin, string? Evidence, DateTime FirstSeenUtc, DateTime LastSeenUtc,
    DiscoveryStatus Status, Guid? AdoptedTargetId, string? AdoptedTargetName);

public static class DiscoveryStatuses
{
    /// <summary>"new" (also null/empty), "dismissed", "adopted" or "all" (status = null), case-insensitive.</summary>
    public static bool TryParseFilter(string? text, out DiscoveryStatus? status)
    {
        status = DiscoveryStatus.New;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "new": return true;
            case "dismissed": status = DiscoveryStatus.Dismissed; return true;
            case "adopted": status = DiscoveryStatus.Adopted; return true;
            case "all": status = null; return true;
            default: status = null; return false;
        }
    }
}

/// <summary>
/// The Discoveries inbox. Upserts on (Address, Kind): a rescan refreshes LastSeen,
/// evidence, method and origin but never changes Status, so a dismissed address
/// stays dismissed. Adoption itself happens in ConfigService, inside the
/// target-create transaction.
/// </summary>
public sealed class DiscoveryStore(IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider? timeProvider = null)
{
    public const int MaxBatch = 512;
    private const int MaxHostname = 255;
    private const int MaxOrigin = 100;
    private const int MaxEvidence = 1000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Every problem in the batch, numbered from 1; empty when the batch is acceptable.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<DiscoveryInput> inputs)
    {
        var errors = new List<string>();
        if (inputs.Count == 0)
            errors.Add("At least one discovery is required.");
        if (inputs.Count > MaxBatch)
            errors.Add($"At most {MaxBatch} discoveries per request.");
        for (var i = 0; i < inputs.Count && i < MaxBatch; i++)
        {
            var item = inputs[i];
            var n = i + 1;
            if (!TryNormalizeAddress(item?.Address, out _))
                errors.Add($"Item {n}: '{item?.Address}' is not a usable IP address.");
            if (item is null)
                continue;
            if (!Enum.IsDefined(item.Kind))
                errors.Add($"Item {n}: unknown kind.");
            if (!Enum.IsDefined(item.Method))
                errors.Add($"Item {n}: unknown method.");
            if (string.IsNullOrWhiteSpace(item.Origin) || item.Origin.Trim().Length > MaxOrigin)
                errors.Add($"Item {n}: origin is required and at most {MaxOrigin} characters.");
            if (item.Hostname is { } h && h.Trim().Length > MaxHostname)
                errors.Add($"Item {n}: hostname is longer than {MaxHostname} characters.");
            if (item.Evidence is { } e && e.Trim().Length > MaxEvidence)
                errors.Add($"Item {n}: evidence is longer than {MaxEvidence} characters.");
        }
        return errors;
    }

    private static bool TryNormalizeAddress(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text.Trim(), out var ip))
            return false;
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            ip.Equals(IPAddress.Broadcast) || ip.Equals(IPAddress.None) ||
            (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes()[0] >= 224) ||
            (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.IsIPv6Multicast))
            return false;
        normalized = ip.ToString();
        return true;
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public async Task<UpsertResult> UpsertAsync(IReadOnlyList<DiscoveryInput> inputs)
    {
        var errors = Validate(inputs);
        if (errors.Count > 0)
            return new UpsertResult(false, errors, 0, 0);

        var now = _time.GetUtcNow().UtcDateTime;
        // Last one wins when a batch names the same (address, kind) twice.
        var batch = new Dictionary<(string Address, DiscoveryKind Kind), DiscoveryInput>();
        foreach (var item in inputs)
        {
            TryNormalizeAddress(item.Address, out var address);
            batch[(address, item.Kind)] = item with
            {
                Address = address, Hostname = Trim(item.Hostname), Origin = item.Origin.Trim(),
                Evidence = Trim(item.Evidence),
            };
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var addresses = batch.Keys.Select(k => k.Address).Distinct().ToList();
        var existing = (await db.Discoveries.Where(d => addresses.Contains(d.Address)).ToListAsync())
            .ToDictionary(d => (d.Address, d.Kind));

        int created = 0, updated = 0;
        foreach (var (key, item) in batch)
        {
            if (existing.TryGetValue(key, out var row))
            {
                row.Method = item.Method;
                row.Origin = item.Origin;
                row.Evidence = item.Evidence;
                row.Hostname = item.Hostname ?? row.Hostname;
                row.LastSeenUtc = now;
                updated++;
            }
            else
            {
                db.Discoveries.Add(new DiscoveryRow
                {
                    Id = Guid.NewGuid(), Address = item.Address, Hostname = item.Hostname,
                    Kind = item.Kind, Method = item.Method, Origin = item.Origin, Evidence = item.Evidence,
                    FirstSeenUtc = now, LastSeenUtc = now, Status = DiscoveryStatus.New,
                });
                created++;
            }
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new UpsertResult(true, [], created, updated);
    }

    /// <param name="status">Null means every status.</param>
    public async Task<List<DiscoveryDto>> ListAsync(DiscoveryStatus? status)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        IQueryable<DiscoveryRow> query = db.Discoveries.AsNoTracking();
        if (status is { } s)
            query = query.Where(d => d.Status == s);
        var rows = await query.OrderByDescending(d => d.LastSeenUtc).ThenBy(d => d.Address).ToListAsync();

        var adoptedIds = rows.Where(r => r.AdoptedTargetId != null).Select(r => r.AdoptedTargetId!.Value).ToList();
        var names = adoptedIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Targets.AsNoTracking().Where(t => adoptedIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name);
        return rows.Select(r => new DiscoveryDto(r.Id, r.Address, r.Hostname, r.Kind, r.Method, r.Origin,
            r.Evidence, r.FirstSeenUtc, r.LastSeenUtc, r.Status, r.AdoptedTargetId,
            r.AdoptedTargetId is { } id && names.TryGetValue(id, out var name) ? name : null)).ToList();
    }

    public async Task<ConfigResult> DismissAsync(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Discoveries.FindAsync(id);
        if (row is null)
            return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No discovery with id {id}.");
        if (await IsLiveAdoptionAsync(db, row))
            return ConfigResult.Fail(ConfigErrorKind.Conflict,
                "This discovery was adopted into a target; delete the target first.");
        row.Status = DiscoveryStatus.Dismissed;
        row.AdoptedTargetId = null;
        await db.SaveChangesAsync();
        return ConfigResult.Ok(id);
    }

    public async Task<ConfigResult> RestoreAsync(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Discoveries.FindAsync(id);
        if (row is null)
            return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No discovery with id {id}.");
        if (await IsLiveAdoptionAsync(db, row))
            return ConfigResult.Fail(ConfigErrorKind.Conflict,
                "This discovery was adopted into a target; delete the target first.");
        row.Status = DiscoveryStatus.New;
        row.AdoptedTargetId = null;
        await db.SaveChangesAsync();
        return ConfigResult.Ok(id);
    }

    /// <summary>Puts every dismissed discovery back to New; returns how many.</summary>
    public async Task<int> RestoreAllDismissedAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Discoveries.Where(d => d.Status == DiscoveryStatus.Dismissed)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DiscoveryStatus.New));
    }

    private static async Task<bool> IsLiveAdoptionAsync(ConWatchDbContext db, DiscoveryRow row) =>
        row.Status == DiscoveryStatus.Adopted && row.AdoptedTargetId is { } target &&
        await db.Targets.AnyAsync(t => t.Id == target);
}
```

- [ ] **Step 6: Run and commit**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug`
Expected: all PASS (the new tests, and `MigrationsTests` proving the migration matches the model).

```bash
git add Core Libraries Tests
git commit -m "feat(discoveries): Discoveries table and DiscoveryStore (upsert, list, dismiss, restore)"
```

---

### Task 2: Adopt a discovery inside the target-create transaction

**Files:**
- Modify: `Libraries/ConPersistence/Config/ConfigService.cs`
- Test: create `Tests/ConPersistence.Tests/DiscoveryAdoptTests.cs`

**Interfaces:**
- Consumes: `DiscoveryRow`, `DiscoveryStore` (Task 1).
- Produces: `ConfigService.UpsertTargetAsync(TargetDto dto, string? addressChange = null, Guid? adoptDiscoveryId = null)`. With `adoptDiscoveryId` set (create only), the new target and the discovery's `Status = Adopted, AdoptedTargetId = new target id` commit atomically; any failure (including the post-save validator) leaves the discovery untouched. Errors: unknown id → `NotFound`; a live adoption → `Conflict`; `dto.Id` set together with `adoptDiscoveryId` → `Validation`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConPersistence.Tests/DiscoveryAdoptTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails (does not compile)**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~DiscoveryAdoptTests"`
Expected: build FAIL — `adoptDiscoveryId` is not a parameter.

- [ ] **Step 3: Implement**

`ConfigService.cs` — add `using ConCore.Discovery;` at the top. Replace the head of `UpsertTargetAsync` and its tail:

```csharp
    public Task<ConfigResult> UpsertTargetAsync(TargetDto dto, string? addressChange = null,
        Guid? adoptDiscoveryId = null) => MutateAsync(async db =>
    {
        // Adopting a discovery creates a NEW target and marks the discovery in the same
        // transaction: if anything below (or the post-save validation) fails, neither happens.
        DiscoveryRow? discovery = null;
        if (adoptDiscoveryId is { } discoveryId)
        {
            if (dto.Id is not null)
                return ConfigResult.Fail(ConfigErrorKind.Validation,
                    "A discovery can only be adopted into a new target.");
            discovery = await db.Discoveries.FindAsync(discoveryId);
            if (discovery is null)
                return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No discovery with id {discoveryId}.");
            if (discovery.Status == DiscoveryStatus.Adopted && discovery.AdoptedTargetId is { } adopted &&
                await db.Targets.AnyAsync(t => t.Id == adopted))
                return ConfigResult.Fail(ConfigErrorKind.Conflict,
                    "This discovery was already adopted into a target.");
        }

        var profile = await db.Profiles.SingleOrDefaultAsync(p => p.Name == dto.Profile);
```

(everything between `var profile…` and `row.CopyFrom(dto, profile.Id, parentId);` stays unchanged), and after `row.CopyFrom(dto, profile.Id, parentId);` add:

```csharp
        if (discovery is not null)
        {
            discovery.Status = DiscoveryStatus.Adopted;
            discovery.AdoptedTargetId = row.Id;
        }
        return ConfigResult.Ok(row.Id);
```

(the existing `return ConfigResult.Ok(row.Id);` line is replaced by the block above).

- [ ] **Step 4: Run and commit**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug`
Expected: all PASS, including the existing `ConfigServiceTests` (the new parameter is optional and trailing).

```bash
git add Libraries Tests
git commit -m "feat(discoveries): adopt a discovery atomically inside the target-create transaction"
```

---

### Task 3: The scanner — router and network-edge searches

**Files:**
- Create: `Libraries/ConInfrastructure/Discovery/DiscoveryScanner.cs`, `Libraries/ConInfrastructure/Discovery/ContainerHint.cs`
- Modify: `Libraries/ConInfrastructure/Traceroute/TracerouteRunner.cs`
- Test: create `Tests/ConEngine.Tests/DiscoveryScannerTests.cs`; extend `Tests/ConEngine.Tests/TracerouteRunnerTests.cs`

**Interfaces:**
- Consumes: `IGatewayDiscovery`, `TracerouteRunner`, `ConCore.ProbePolicy.IsPrivateOrCarrier(string)`, the enums (Task 1).
- Produces: `DiscoveryCandidate(string Address, string? Hostname, DiscoveryKind Kind, DiscoveryMethod Method, string? Evidence)`; `IDiscoveryScanner { FindRouterAsync(ct); FindNetworkEdgeAsync(string host, ct) }` (both return `IReadOnlyList<DiscoveryCandidate>`); `DiscoveryScanner(IGatewayDiscovery, TracerouteRunner)` with `DefaultEdgeHost = "1.1.1.1"`; `ContainerHint.IsLikelyContainer()` (plus a testable overload); `TracerouteRunner.RunAsync(string host, bool lookupOperators, Func<TracerouteHop,bool>? stopWhen, CancellationToken ct)` — `stopWhen` is called with each raw (unclassified) hop and returning true ends the walk after that hop. LAN scanning is plan 3 and is **not** added here.
- Rules: the router search keeps a candidate only if it came from the default route (even if it does not answer pings) **or** answers pings; the edge search is the first answering hop with a non-private, non-carrier address, and **nothing** when that hop is the destination itself (no intermediate public hop was seen).

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/DiscoveryScannerTests.cs`:

```csharp
using System.Net;
using ConCore.Discovery;
using ConInfrastructure.Asn;
using ConInfrastructure.Discovery;
using ConInfrastructure.Traceroute;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class DiscoveryScannerTests
{
    private sealed class FakeGateways(params GatewayCandidate[] candidates) : IGatewayDiscovery
    {
        public Task<IReadOnlyList<GatewayCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GatewayCandidate>>(candidates);
    }

    /// <summary>Hop addresses per TTL (null = silent); beyond the list the destination answers.</summary>
    private sealed class ScriptProbe(params string?[] hops) : ITtlProbe
    {
        public int HighestTtl { get; private set; }

        public Task<TtlProbeResult> ProbeAsync(IPAddress destination, int ttl, int timeoutMs, CancellationToken ct)
        {
            HighestTtl = Math.Max(HighestTtl, ttl);
            if (ttl > hops.Length)
                return Task.FromResult(new TtlProbeResult(destination, 30, ReachedDestination: true, TimedOut: false));
            var hop = hops[ttl - 1];
            if (hop is null)
                return Task.FromResult(new TtlProbeResult(null, 0, false, TimedOut: true));
            return Task.FromResult(new TtlProbeResult(IPAddress.Parse(hop), ttl * 10, false, false));
        }
    }

    private static DiscoveryScanner Scanner(IGatewayDiscovery gateways, ScriptProbe? probe = null) =>
        new(gateways, new TracerouteRunner(probe ?? new ScriptProbe(), NullAsnLookup.Instance,
            NullLogger<TracerouteRunner>.Instance));

    [Fact]
    public async Task Router_search_keeps_the_default_route_even_without_a_ping_reply_and_reachable_probes()
    {
        var scanner = Scanner(new FakeGateways(
            new GatewayCandidate("192.0.2.1", "default-route", false, null),
            new GatewayCandidate("192.0.2.254", "probe", true, 4),
            new GatewayCandidate("198.51.100.1", "probe", false, null)));

        var found = await scanner.FindRouterAsync(CancellationToken.None);

        Assert.Equal(["192.0.2.1", "192.0.2.254"], found.Select(c => c.Address));
        Assert.All(found, c => Assert.Equal(DiscoveryKind.Gateway, c.Kind));
        Assert.Equal(DiscoveryMethod.DefaultRoute, found[0].Method);
        Assert.Contains("does not answer", found[0].Evidence);
        Assert.Equal(DiscoveryMethod.CommonAddress, found[1].Method);
        Assert.Contains("4 ms", found[1].Evidence);
    }

    [Fact]
    public async Task Router_search_with_nothing_found_returns_an_empty_list()
    {
        var scanner = Scanner(new FakeGateways(new GatewayCandidate("198.51.100.1", "probe", false, null)));
        Assert.Empty(await scanner.FindRouterAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Edge_search_finds_the_first_public_hop_and_stops_walking_there()
    {
        var probe = new ScriptProbe("192.168.1.1", "100.64.0.1", "203.0.113.5", "203.0.113.6", "203.0.113.7");
        var scanner = Scanner(new FakeGateways(), probe);

        var found = await scanner.FindNetworkEdgeAsync("198.51.100.77", CancellationToken.None);

        var edge = Assert.Single(found);
        Assert.Equal("203.0.113.5", edge.Address);
        Assert.Equal(DiscoveryKind.NetworkEdge, edge.Kind);
        Assert.Equal(DiscoveryMethod.Traceroute, edge.Method);
        Assert.Contains("hop 3", edge.Evidence);
        Assert.Contains("198.51.100.77", edge.Evidence);
        Assert.Equal(3, probe.HighestTtl);   // the walk stopped at the edge; it did not trace to the destination
    }

    [Fact]
    public async Task Edge_search_skips_silent_hops_and_carrier_addresses()
    {
        var probe = new ScriptProbe("192.168.1.1", null, "100.64.0.9", "203.0.113.5");
        var found = await Scanner(new FakeGateways(), probe)
            .FindNetworkEdgeAsync("198.51.100.77", CancellationToken.None);
        Assert.Equal("203.0.113.5", Assert.Single(found).Address);
    }

    [Fact]
    public async Task Edge_search_finds_nothing_when_the_first_public_address_is_the_destination_itself()
    {
        // Only private hops, then the destination answers: no intermediate public hop was seen.
        var probe = new ScriptProbe("192.168.1.1", "10.0.0.1");
        var found = await Scanner(new FakeGateways(), probe)
            .FindNetworkEdgeAsync("198.51.100.77", CancellationToken.None);
        Assert.Empty(found);
    }

    [Fact]
    public async Task Edge_search_with_an_unresolvable_host_throws_for_the_caller_to_report()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Scanner(new FakeGateways())
            .FindNetworkEdgeAsync("definitely-not-a-real-host.invalid", CancellationToken.None));
    }

    [Fact]
    public void ContainerHint_reads_the_dotnet_flag_or_the_docker_marker_file()
    {
        Assert.True(ContainerHint.IsLikelyContainer(k => k == "DOTNET_RUNNING_IN_CONTAINER" ? "true" : null, _ => false));
        Assert.True(ContainerHint.IsLikelyContainer(_ => null, p => p == "/.dockerenv"));
        Assert.False(ContainerHint.IsLikelyContainer(_ => null, _ => false));
        Assert.False(ContainerHint.IsLikelyContainer(k => k == "DOTNET_RUNNING_IN_CONTAINER" ? "false" : null, _ => false));
    }
}
```

Append to `Tests/ConEngine.Tests/TracerouteRunnerTests.cs` (inside the class, using its existing `FakeProbe`):

```csharp
    [Fact]
    public async Task StopWhen_ends_the_walk_after_the_hop_that_satisfies_it()
    {
        var runner = new TracerouteRunner(new FakeProbe("10.0.0.1", "198.51.100.11", "198.51.100.12", "8.8.8.8"),
            NullAsnLookup.Instance, NullLogger<TracerouteRunner>.Instance);

        var hops = await runner.RunAsync("8.8.8.8", lookupOperators: false, stopWhen: h => h.Ttl == 2, CancellationToken.None);

        Assert.Equal([1, 2], hops.Select(h => h.Ttl));
        Assert.False(hops.Last().ReachedDestination);
        Assert.NotNull(hops.Last().Kind);   // the stopped walk is still classified
    }
```

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~DiscoveryScannerTests|FullyQualifiedName~StopWhen"`
Expected: build FAIL — `DiscoveryScanner`, `ContainerHint` and the `stopWhen` overload do not exist.

- [ ] **Step 2: Early-stop traceroute**

In `TracerouteRunner.cs`, replace the first public overload and the signature of the second, and the end-of-hop check:

```csharp
    public Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, bool lookupOperators, CancellationToken ct) =>
        RunAsync(host, lookupOperators, stopWhen: null, ct);

    /// <param name="lookupOperators">When true, every Path/Destination hop with a
    /// public address is sent to <see cref="IAsnLookup"/> after the walk.</param>
    /// <param name="stopWhen">Called with each raw (not yet classified) hop; returning true
    /// ends the walk after that hop. Null walks to the destination or TTL 30.</param>
    public async Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, bool lookupOperators,
        Func<TracerouteHop, bool>? stopWhen, CancellationToken ct)
```

and in the loop, replace

```csharp
            hops.Add(new TracerouteHop(ttl, hopAddress?.ToString(),
                hopAddress is null ? null : await ReverseDnsAsync(hopAddress, ct).ConfigureAwait(false),
                bestRtt, reached, null, null, null));
            if (reached)
                break;
```

with

```csharp
            var hopRow = new TracerouteHop(ttl, hopAddress?.ToString(),
                hopAddress is null ? null : await ReverseDnsAsync(hopAddress, ct).ConfigureAwait(false),
                bestRtt, reached, null, null, null);
            hops.Add(hopRow);
            if (reached || (stopWhen?.Invoke(hopRow) ?? false))
                break;
```

- [ ] **Step 3: Container hint and scanner**

Create `Libraries/ConInfrastructure/Discovery/ContainerHint.cs`:

```csharp
namespace ConInfrastructure.Discovery;

/// <summary>Best-effort "are we inside a container?" so searches can warn that the
/// default gateway may be the container bridge rather than the user's router.</summary>
public static class ContainerHint
{
    public const string Warning =
        "This server appears to be running in a container, so the default gateway it sees may be the " +
        "container's bridge rather than your router. Check the address before adopting it.";

    public static bool IsLikelyContainer() => IsLikelyContainer(Environment.GetEnvironmentVariable, File.Exists);

    public static bool IsLikelyContainer(Func<string, string?> env, Func<string, bool> fileExists) =>
        string.Equals(env("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
        fileExists("/.dockerenv");
}
```

Create `Libraries/ConInfrastructure/Discovery/DiscoveryScanner.cs`:

```csharp
using ConCore;
using ConCore.Discovery;
using ConInfrastructure.Traceroute;

namespace ConInfrastructure.Discovery;

/// <summary>One candidate a search found, before it is stored as a discovery.</summary>
public sealed record DiscoveryCandidate(string Address, string? Hostname, DiscoveryKind Kind,
    DiscoveryMethod Method, string? Evidence);

/// <summary>The searches a node can run on its own network vantage. Results always need user confirmation.</summary>
public interface IDiscoveryScanner
{
    Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct);

    /// <summary>The first hop beyond the LAN on the way to <paramref name="host"/>; empty when none was seen.
    /// Throws for an unresolvable host (the caller reports it).</summary>
    Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct);
}

public sealed class DiscoveryScanner(IGatewayDiscovery gateways, TracerouteRunner traceroute) : IDiscoveryScanner
{
    /// <summary>A public anycast address already used as a default anchor; the walk stops long before it.</summary>
    public const string DefaultEdgeHost = "1.1.1.1";

    public async Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct)
    {
        var found = await gateways.DiscoverAsync(ct).ConfigureAwait(false);
        return found
            .Where(g => g.Source == "default-route" || g.Reachable)
            .Select(g => new DiscoveryCandidate(g.Address, null, DiscoveryKind.Gateway,
                g.Source == "default-route" ? DiscoveryMethod.DefaultRoute : DiscoveryMethod.CommonAddress,
                g.Reachable ? $"answers pings in {g.RttMs} ms" : "the default route, but it does not answer pings"))
            .ToList();
    }

    public async Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct)
    {
        static bool IsPublic(TracerouteHop hop) => hop.Address is { } a && !ProbePolicy.IsPrivateOrCarrier(a);

        var hops = await traceroute.RunAsync(host, lookupOperators: false, stopWhen: IsPublic, ct)
            .ConfigureAwait(false);
        var edge = hops.FirstOrDefault(IsPublic);
        // The destination answering first means no intermediate public hop was seen.
        if (edge is null || edge.ReachedDestination)
            return [];
        return [new DiscoveryCandidate(edge.Address!, edge.Hostname, DiscoveryKind.NetworkEdge,
            DiscoveryMethod.Traceroute,
            $"hop {edge.Ttl} toward {host}" + (edge.RttMs is { } rtt ? $", {rtt} ms" : ""))];
    }
}
```

- [ ] **Step 4: Run and commit**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: all PASS (the existing traceroute and gateway tests too).

```bash
git add Libraries Tests
git commit -m "feat(discoveries): router and network-edge scanner; traceroute can stop at the first public hop"
```

---

### Task 4: Serialized searches, the REST surface, DI

**Files:**
- Create: `Programs/ConWatch/Discoveries/DiscoverySearchService.cs`, `Programs/ConWatch/Api/DiscoveryEndpoints.cs`
- Modify: `Programs/ConWatch/Api/ConfigEndpoints.cs` (adopt on `POST /targets`), `Programs/ConWatch/ConWatchApp.cs`, `Programs/ConWatch/Startup/ConWatchServices.cs`
- Test: create `Tests/ConWatch.Tests/DiscoveryApiTests.cs`, `Tests/ConWatch.Tests/DiscoverySearchServiceTests.cs`; `Tests/ConWatch.Tests/ApiSurfaceLockTests.cs` should need no edit — run it

**Interfaces:**
- Consumes: `DiscoveryStore`, `DiscoveryStatuses` (Task 1), `ConfigService.UpsertTargetAsync(…, adoptDiscoveryId)` (Task 2), `IDiscoveryScanner`, `ContainerHint` (Task 3).
- Produces (all on the **locked** group):
  - `GET /api/v1/discoveries?status=new|dismissed|adopted|all` (default `new`) → array of `DiscoveryDto`; bad status → 400.
  - `POST /api/v1/discoveries` body `{ "items": [DiscoveryInput…] }` → `200 { created, updated }`; validation problems → 400 with every message.
  - `POST /api/v1/discoveries/search/router` and `POST /api/v1/discoveries/search/edge` (optional body `{ "host": "…" }`) → `200 { found, created, updated, warning }`; `409` code `search-busy`; `504` code `search-timeout`; `400` for an unresolvable host.
  - `POST /api/v1/discoveries/{id}/dismiss`, `POST /api/v1/discoveries/{id}/restore` → `204` / `404` / `409`; `POST /api/v1/discoveries/restore-dismissed` → `200 { restored }`.
  - `POST /api/v1/targets?discoveryId=<guid>` adopts (Task 2 semantics: `404` unknown, `409` live adoption).
  - `DiscoverySearchService(IDiscoveryScanner, DiscoveryStore, ILogger<DiscoverySearchService>, TimeSpan? cap = null)` with `SearchRouterAsync(ct)`, `SearchEdgeAsync(string? host, ct)` → `SearchOutcome(bool Busy, bool TimedOut, int Found, int Created, int Updated, string? Warning, IReadOnlyList<string> Errors)`; `Cap` defaults to 30 s.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConWatch.Tests/DiscoverySearchServiceTests.cs`:

```csharp
using ConCore.Discovery;
using ConInfrastructure.Discovery;
using ConPersistence.Discoveries;
using ConWatch.Discoveries;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConWatch.Tests;

public class DiscoverySearchServiceTests : IDisposable
{
    private sealed class HangingScanner : IDiscoveryScanner
    {
        public Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct) => Hang(ct);
        public Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct) => Hang(ct);
        private static async Task<IReadOnlyList<DiscoveryCandidate>> Hang(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
    }

    private sealed class ThrowingScanner : IDiscoveryScanner
    {
        public Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct) =>
            throw new ArgumentException($"No IPv4 address for '{host}'.");
    }

    /// <summary>In-memory SQLite with the real schema (ConWatch.Tests cannot see ConPersistence.Tests' factory).</summary>
    private sealed class MemoryDb : Microsoft.EntityFrameworkCore.IDbContextFactory<ConPersistence.ConWatchDbContext>, IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("Data Source=:memory:");
        private readonly Microsoft.EntityFrameworkCore.DbContextOptions<ConPersistence.ConWatchDbContext> _options;

        public MemoryDb()
        {
            _connection.Open();
            _options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ConPersistence.ConWatchDbContext>()
                .UseSqlite(_connection).Options;
            using var db = new ConPersistence.ConWatchDbContext(_options);
            db.Database.EnsureCreated();
        }

        public ConPersistence.ConWatchDbContext CreateDbContext() => new(_options);
        public void Dispose() => _connection.Dispose();
    }

    private readonly MemoryDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_search_that_exceeds_the_cap_reports_a_timeout_and_frees_the_gate()
    {
        var service = new DiscoverySearchService(new HangingScanner(), new DiscoveryStore(_db),
            NullLogger<DiscoverySearchService>.Instance, cap: TimeSpan.FromMilliseconds(100));

        var first = await service.SearchRouterAsync(CancellationToken.None);
        var second = await service.SearchRouterAsync(CancellationToken.None);   // the gate was released

        Assert.True(first.TimedOut);
        Assert.False(first.Busy);
        Assert.True(second.TimedOut);
        Assert.False(second.Busy);
    }

    [Fact]
    public async Task An_unresolvable_edge_host_is_an_error_not_a_crash_and_frees_the_gate()
    {
        var service = new DiscoverySearchService(new ThrowingScanner(), new DiscoveryStore(_db),
            NullLogger<DiscoverySearchService>.Instance);

        var outcome = await service.SearchEdgeAsync("nope.invalid", CancellationToken.None);

        Assert.Contains(outcome.Errors, e => e.Contains("nope.invalid"));
        Assert.False(outcome.Busy);
    }
}
```

(If `Microsoft.Data.Sqlite` is not directly referenced by `ConWatch.Tests`, the `ConWatch` project already brings it in transitively; if the compiler cannot find it, add the same `PackageReference` the persistence tests use.)

Create `Tests/ConWatch.Tests/DiscoveryApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Discovery;
using ConInfrastructure.Discovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConWatch.Tests;

public class DiscoveryApiTests : IDisposable
{
    /// <summary>Scripted candidates; the edge search can be held open to test the busy gate.</summary>
    private sealed class FakeScanner : IDiscoveryScanner
    {
        public static List<DiscoveryCandidate> Router { get; } =
        [
            new("192.0.2.1", null, DiscoveryKind.Gateway, DiscoveryMethod.DefaultRoute, "answers pings in 1 ms"),
        ];

        public static TaskCompletionSource<bool>? HoldEdge { get; set; }
        public static TaskCompletionSource<bool> EdgeStarted { get; set; } = new();

        public Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveryCandidate>>(Router);

        public async Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct)
        {
            EdgeStarted.TrySetResult(true);
            if (HoldEdge is { } hold)
                await hold.Task.WaitAsync(ct);
            return [new("203.0.113.5", null, DiscoveryKind.NetworkEdge, DiscoveryMethod.Traceroute, $"hop 3 toward {host}")];
        }
    }

    private sealed class Factory : ConWatchFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDiscoveryScanner>();
                services.AddSingleton<IDiscoveryScanner, FakeScanner>();
            });
        }
    }

    private readonly Factory _factory = new();
    private readonly HttpClient _client;

    public DiscoveryApiTests()
    {
        FakeScanner.HoldEdge = null;
        FakeScanner.EdgeStarted = new();
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static List<JsonElement> Items(JsonElement array) => array.EnumerateArray().ToList();

    [Fact]
    public async Task Router_search_stores_candidates_as_New_and_a_rescan_updates_instead_of_duplicating()
    {
        var first = await _client.PostAsync("/api/v1/discoveries/search/router", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await first.Content.ReadAsStringAsync());
        Assert.Equal(1, body.GetProperty("found").GetInt32());
        Assert.Equal(1, body.GetProperty("created").GetInt32());

        var list = Items(await GetJson("/api/v1/discoveries"));
        var row = Assert.Single(list);
        Assert.Equal("192.0.2.1", row.GetProperty("address").GetString());
        Assert.Equal("Gateway", row.GetProperty("kind").GetString());
        Assert.Equal("DefaultRoute", row.GetProperty("method").GetString());
        Assert.Equal("server", row.GetProperty("origin").GetString());
        Assert.Equal("New", row.GetProperty("status").GetString());

        var second = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsync("/api/v1/discoveries/search/router", null)).Content.ReadAsStringAsync());
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(1, second.GetProperty("updated").GetInt32());
        Assert.Single(Items(await GetJson("/api/v1/discoveries?status=all")));
    }

    [Fact]
    public async Task Dismiss_hides_it_from_the_default_view_and_rediscovery_does_not_resurrect_it()
    {
        await _client.PostAsync("/api/v1/discoveries/search/router", null);
        var id = Items(await GetJson("/api/v1/discoveries")).Single().GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/api/v1/discoveries/{id}/dismiss", null)).StatusCode);
        Assert.Empty(Items(await GetJson("/api/v1/discoveries")));
        Assert.Single(Items(await GetJson("/api/v1/discoveries?status=dismissed")));

        await _client.PostAsync("/api/v1/discoveries/search/router", null);   // found again
        Assert.Empty(Items(await GetJson("/api/v1/discoveries")));
        Assert.Single(Items(await GetJson("/api/v1/discoveries?status=dismissed")));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/api/v1/discoveries/{id}/restore", null)).StatusCode);
        Assert.Single(Items(await GetJson("/api/v1/discoveries")));
    }

    [Fact]
    public async Task Restore_all_dismissed_reports_how_many()
    {
        await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new[]
            {
                new { address = "192.0.2.10", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
                new { address = "192.0.2.11", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
            },
        });
        foreach (var d in Items(await GetJson("/api/v1/discoveries")))
            await _client.PostAsync($"/api/v1/discoveries/{d.GetProperty("id").GetString()}/dismiss", null);

        var restored = JsonSerializer.Deserialize<JsonElement>(await (await _client
            .PostAsync("/api/v1/discoveries/restore-dismissed", null)).Content.ReadAsStringAsync());

        Assert.Equal(2, restored.GetProperty("restored").GetInt32());
        Assert.Equal(2, Items(await GetJson("/api/v1/discoveries")).Count);
    }

    [Fact]
    public async Task Unknown_ids_are_404_and_a_bad_status_is_400()
    {
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/v1/discoveries/{missing}/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/v1/discoveries/{missing}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/discoveries?status=bogus")).StatusCode);
    }

    [Fact]
    public async Task A_batch_post_stores_valid_items_and_reports_created_and_updated()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new object[]
            {
                new { address = "192.0.2.50", hostname = "nas.example.com", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC", evidence = "aa:bb:cc vendor" },
                new { address = "192.0.2.1", kind = "Gateway", method = "DefaultRoute", origin = "tray:PC" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.GetProperty("created").GetInt32());

        var again = JsonSerializer.Deserialize<JsonElement>(await (await _client.PostAsJsonAsync("/api/v1/discoveries",
            new { items = new[] { new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" } } }))
            .Content.ReadAsStringAsync());
        Assert.Equal(1, again.GetProperty("updated").GetInt32());
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"items":[{"address":"router.example.com","kind":"Gateway","method":"DefaultRoute","origin":"x"}]}""")]
    [InlineData("""{"items":[{"address":"127.0.0.1","kind":"Gateway","method":"DefaultRoute","origin":"x"}]}""")]
    [InlineData("""{"items":[{"address":"192.0.2.1","kind":"Gateway","method":"DefaultRoute"}]}""")]
    [InlineData("""{"items":[{"address":"192.0.2.1","kind":9,"method":"DefaultRoute","origin":"x"}]}""")]
    [InlineData("""{"items":[{"address":"192.0.2.1","kind":"Bogus","method":"DefaultRoute","origin":"x"}]}""")]
    public async Task A_bad_batch_is_a_400_and_writes_nothing(string json)
    {
        var response = await _client.PostAsync("/api/v1/discoveries",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Items(await GetJson("/api/v1/discoveries?status=all")));
    }

    [Fact]
    public async Task Adopting_via_POST_targets_marks_the_discovery_and_a_failed_adoption_does_not()
    {
        await ConfigApiTests.SeedHomeAsync(_client);                       // Router 192.168.1.1 etc.
        await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new[]
            {
                new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
                new { address = "192.168.1.1", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
            },
        });
        var byAddress = Items(await GetJson("/api/v1/discoveries"))
            .ToDictionary(d => d.GetProperty("address").GetString()!, d => d.GetProperty("id").GetString()!);

        // A target on an address that already exists fails validation: the discovery stays New.
        var clash = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={byAddress["192.168.1.1"]}",
            new { name = "Dup", address = "192.168.1.1", profile = "Fast", scope = "Local" });
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        Assert.Contains(Items(await GetJson("/api/v1/discoveries")), d => d.GetProperty("address").GetString() == "192.168.1.1");

        var ok = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={byAddress["192.0.2.50"]}",
            new { name = "NasBox", address = "192.0.2.50", profile = "Fast", scope = "Local" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var adopted = Assert.Single(Items(await GetJson("/api/v1/discoveries?status=adopted")));
        Assert.Equal("NasBox", adopted.GetProperty("adoptedTargetName").GetString());

        // Adopting it again while the target exists is a conflict; an unknown id is a 404.
        var again = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={byAddress["192.0.2.50"]}",
            new { name = "NasBox2", address = "192.0.2.51", profile = "Fast", scope = "Local" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var unknown = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={Guid.NewGuid()}",
            new { name = "X", address = "192.0.2.52", profile = "Fast" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_adopted_target_leaves_the_discovery_Adopted_without_a_name_and_it_can_be_dismissed()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new[] { new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" } },
        });
        var id = Items(await GetJson("/api/v1/discoveries")).Single().GetProperty("id").GetString();
        var created = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={id}",
            new { name = "NasBox", address = "192.0.2.50", profile = "Fast", scope = "Local" });
        var targetId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("id").GetString();

        // Live adoption: dismiss is a 409.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/v1/discoveries/{id}/dismiss", null)).StatusCode);

        (await _client.DeleteAsync($"/api/v1/targets/{targetId}")).EnsureSuccessStatusCode();
        var row = Assert.Single(Items(await GetJson("/api/v1/discoveries?status=adopted")));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("adoptedTargetName").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/api/v1/discoveries/{id}/dismiss", null)).StatusCode);
    }

    [Fact]
    public async Task A_second_search_while_one_is_running_is_a_409_and_the_first_still_stores_its_result()
    {
        FakeScanner.HoldEdge = new TaskCompletionSource<bool>();
        var running = _client.PostAsync("/api/v1/discoveries/search/edge", null);
        await FakeScanner.EdgeStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));   // it holds the gate now

        var busy = await _client.PostAsync("/api/v1/discoveries/search/router", null);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains("search-busy", await busy.Content.ReadAsStringAsync());

        FakeScanner.HoldEdge.SetResult(true);
        var finished = await running;
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        var edge = Assert.Single(Items(await GetJson("/api/v1/discoveries")));
        Assert.Equal("NetworkEdge", edge.GetProperty("kind").GetString());
        Assert.Contains("1.1.1.1", edge.GetProperty("evidence").GetString());   // the default host
    }

    [Fact]
    public async Task Every_discovery_route_is_locked_when_an_admin_password_is_set()
    {
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        var anon = _factory.CreateClient();
        var id = Guid.NewGuid();

        foreach (var (method, url) in new[]
        {
            ("GET", "/api/v1/discoveries"), ("POST", "/api/v1/discoveries"),
            ("POST", "/api/v1/discoveries/search/router"), ("POST", "/api/v1/discoveries/search/edge"),
            ("POST", "/api/v1/discoveries/restore-dismissed"),
            ("POST", $"/api/v1/discoveries/{id}/dismiss"), ("POST", $"/api/v1/discoveries/{id}/restore"),
        })
        {
            var response = await anon.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = method == "POST" ? JsonContent.Create(new { }) : null,
            });
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {url} was {(int)response.StatusCode}");
        }
    }
}
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~DiscoveryApiTests|FullyQualifiedName~DiscoverySearchServiceTests"`
Expected: build FAIL — `DiscoverySearchService`, the endpoints and DI registrations do not exist.

- [ ] **Step 2: The search service**

Create `Programs/ConWatch/Discoveries/DiscoverySearchService.cs`:

```csharp
using System.Net.Sockets;
using ConInfrastructure.Discovery;
using ConPersistence.Discoveries;

namespace ConWatch.Discoveries;

/// <param name="Busy">Another search holds the gate.</param>
/// <param name="TimedOut">The search ran past the cap; nothing was stored.</param>
public sealed record SearchOutcome(bool Busy, bool TimedOut, int Found, int Created, int Updated,
    string? Warning, IReadOnlyList<string> Errors)
{
    public static SearchOutcome IsBusy { get; } = new(true, false, 0, 0, 0, null, []);
}

/// <summary>
/// Runs the node's own searches one at a time with an overall time cap, and files
/// what they find in the inbox with Origin "server". The cap and the gate are what
/// keep a stuck traceroute from wedging the page.
/// </summary>
public sealed class DiscoverySearchService(IDiscoveryScanner scanner, DiscoveryStore store,
    ILogger<DiscoverySearchService> log, TimeSpan? cap = null)
{
    public const string ServerOrigin = "server";
    public static readonly TimeSpan DefaultCap = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _cap = cap ?? DefaultCap;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<SearchOutcome> SearchRouterAsync(CancellationToken ct) =>
        RunAsync(t => scanner.FindRouterAsync(t), ct);

    /// <param name="host">The public address to trace toward; null or blank uses <see cref="DiscoveryScanner.DefaultEdgeHost"/>.</param>
    public Task<SearchOutcome> SearchEdgeAsync(string? host, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(host) ? DiscoveryScanner.DefaultEdgeHost : host.Trim();
        return RunAsync(t => scanner.FindNetworkEdgeAsync(target, t), ct);
    }

    private async Task<SearchOutcome> RunAsync(Func<CancellationToken, Task<IReadOnlyList<DiscoveryCandidate>>> search,
        CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, CancellationToken.None))
            return SearchOutcome.IsBusy;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_cap);
            IReadOnlyList<DiscoveryCandidate> found;
            try
            {
                found = await search(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Discovery search exceeded its {Cap} cap", _cap);
                return new SearchOutcome(false, true, 0, 0, 0, Warning(), []);
            }
            catch (Exception ex) when (ex is ArgumentException or SocketException)
            {
                return new SearchOutcome(false, false, 0, 0, 0, null, [$"Cannot search: {ex.Message}"]);
            }

            if (found.Count == 0)
                return new SearchOutcome(false, false, 0, 0, 0, Warning(), []);
            var stored = await store.UpsertAsync(found.Select(c => new DiscoveryInput(
                c.Address, c.Hostname, c.Kind, c.Method, ServerOrigin, c.Evidence)).ToList());
            return stored.Success
                ? new SearchOutcome(false, false, found.Count, stored.Created, stored.Updated, Warning(), [])
                : new SearchOutcome(false, false, 0, 0, 0, null, stored.Errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Warning() => ContainerHint.IsLikelyContainer() ? ContainerHint.Warning : null;
}
```

- [ ] **Step 3: The endpoints**

Create `Programs/ConWatch/Api/DiscoveryEndpoints.cs`:

```csharp
using ConPersistence.Discoveries;
using ConWatch.Discoveries;

namespace ConWatch.Api;

/// <summary>The Discoveries inbox. Everything here is admin-gated (mapped on the locked group);
/// the only open view of discoveries is the read-only MCP tool.</summary>
public static class DiscoveryEndpoints
{
    public record UpsertRequest(List<DiscoveryInput>? Items);

    public record EdgeSearchRequest(string? Host);

    public static void MapDiscoveries(this RouteGroupBuilder locked)
    {
        var d = locked.MapGroup("/discoveries");

        d.MapGet("/", async (DiscoveryStore store, string? status) =>
            DiscoveryStatuses.TryParseFilter(status, out var filter)
                ? Results.Ok(await store.ListAsync(filter))
                : ApiResults.Error(StatusCodes.Status400BadRequest, null,
                    "'status' must be new, dismissed, adopted or all."));

        d.MapPost("/", async (UpsertRequest request, DiscoveryStore store) =>
        {
            var result = await store.UpsertAsync((IReadOnlyList<DiscoveryInput>?)request.Items ?? []);
            return result.Success
                ? Results.Ok(new { created = result.Created, updated = result.Updated })
                : ApiResults.Error(StatusCodes.Status400BadRequest, null, result.Errors.ToArray());
        });

        d.MapPost("/search/router", async (DiscoverySearchService search, CancellationToken ct) =>
            ToHttp(await search.SearchRouterAsync(ct)));

        d.MapPost("/search/edge", async (EdgeSearchRequest? request, DiscoverySearchService search,
            CancellationToken ct) => ToHttp(await search.SearchEdgeAsync(request?.Host, ct)));

        d.MapPost("/restore-dismissed", async (DiscoveryStore store) =>
            Results.Ok(new { restored = await store.RestoreAllDismissedAsync() }));

        d.MapPost("/{id:guid}/dismiss", async (Guid id, DiscoveryStore store) =>
            (await store.DismissAsync(id)).ToHttp());

        d.MapPost("/{id:guid}/restore", async (Guid id, DiscoveryStore store) =>
            (await store.RestoreAsync(id)).ToHttp());
    }

    private static IResult ToHttp(SearchOutcome outcome)
    {
        if (outcome.Busy)
            return ApiResults.Error(StatusCodes.Status409Conflict, "search-busy",
                "Another search is already running; try again in a moment.");
        if (outcome.TimedOut)
            return ApiResults.Error(StatusCodes.Status504GatewayTimeout, "search-timeout",
                "The search took too long and was stopped; nothing was found.");
        if (outcome.Errors.Count > 0)
            return ApiResults.Error(StatusCodes.Status400BadRequest, null, outcome.Errors.ToArray());
        return Results.Ok(new
        {
            found = outcome.Found, created = outcome.Created, updated = outcome.Updated,
            warning = outcome.Warning,
        });
    }
}
```

`ConfigEndpoints.cs` — replace the `POST /targets` handler so a query `discoveryId` adopts:

```csharp
        targets.MapPost("/", async (TargetDto dto, ConfigService service, Guid? discoveryId) =>
            (await service.UpsertTargetAsync(dto with { Id = null }, adoptDiscoveryId: discoveryId))
                .ToHttp(r => Results.Created($"/api/v1/targets/{r.Id}", new { id = r.Id })));
```

`ConWatchApp.cs` — beside `locked.MapTraceroute();` add `locked.MapDiscoveries();`.

`Startup/ConWatchServices.cs` — beside the `IGatewayDiscovery` registration (and after the `ConfigService` line) add:

```csharp
        services.AddSingleton<ConPersistence.Discoveries.DiscoveryStore>();
        services.AddSingleton<ConInfrastructure.Discovery.IDiscoveryScanner, ConInfrastructure.Discovery.DiscoveryScanner>();
        services.AddSingleton<Discoveries.DiscoverySearchService>();
```

- [ ] **Step 4: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~DiscoveryApiTests|FullyQualifiedName~DiscoverySearchServiceTests|FullyQualifiedName~ApiSurfaceLockTests"` then the full `Tests/ConWatch.Tests`.
Expected: all PASS. `ApiSurfaceLockTests` must pass **unchanged** (every new route is locked); if it fails, a route was mapped on the open group by mistake — fix the mapping, never the allowlist.

```bash
git add Programs Tests
git commit -m "feat(discoveries): REST surface, serialized searches with a time cap, adopt via POST /targets?discoveryId"
```

---

### Task 5: The Config page — list, search, dismiss, restore, adopt

**Files:**
- Create: `Programs/ConWatch/Pages/Config/Discoveries.cshtml`, `Programs/ConWatch/Pages/Config/Discoveries.cshtml.cs`
- Modify: `Programs/ConWatch/Pages/Config/_ConfigTabs.cshtml`, `Programs/ConWatch/Pages/Config/Targets.cshtml`, `Programs/ConWatch/Pages/Config/Targets.cshtml.cs`
- Test: create `Tests/ConWatch.Tests/DiscoveriesPageTests.cs`

**Interfaces:**
- Consumes: `DiscoveryStore`/`DiscoveryDto`/`DiscoveryStatuses` (Task 1), the REST routes (Task 4), the Targets dialog prefill (`address`, `name`, `profile`, `scope`; plan 1B).
- Produces: `/Config/Discoveries[?status=new|dismissed|adopted|all]`; each row's **Adopt** link is `/Config/Targets?address=…&name=…&profile=…&scope=…&discoveryId=<guid>` with defaults by kind — Gateway → name `Router`, profile `Internal`, scope `Verdict`; NetworkEdge → `ISP edge`, `Path`, `Verdict`; LocalDevice → hostname (or address), `Local`, `Local` — the name made unique against existing target names (`Router 2`, …). The Targets page accepts `?discoveryId=<guid>` (a malformed value is ignored) and, when the user saves the prefilled **new** target, posts `POST /api/v1/targets?discoveryId=…`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConWatch.Tests/DiscoveriesPageTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ConWatch.Tests;

public class DiscoveriesPageTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public DiscoveriesPageTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task PostAsync(params object[] items) =>
        (await _client.PostAsJsonAsync("/api/v1/discoveries", new { items })).EnsureSuccessStatusCode();

    private static object Item(string address, string kind, string method = "ArpTable", string? hostname = null) =>
        new { address, hostname, kind, method, origin = "tray:PC", evidence = "test evidence" };

    private async Task<string> IdOfAsync(string address)
    {
        var all = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/discoveries?status=all"));
        return all.EnumerateArray().Single(d => d.GetProperty("address").GetString() == address)
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_page_lists_new_discoveries_with_kind_method_origin_and_evidence()
    {
        await PostAsync(Item("192.0.2.1", "Gateway", "DefaultRoute"), Item("192.0.2.50", "LocalDevice", hostname: "nas.example.com"));

        var html = await _client.GetStringAsync("/Config/Discoveries");

        Assert.Contains("192.0.2.1", html);
        Assert.Contains("Router (gateway)", html);
        Assert.Contains("Local device", html);
        Assert.Contains("nas.example.com", html);
        Assert.Contains("tray:PC", html);
        Assert.Contains("test evidence", html);
        Assert.Contains("Find my router", html);
        Assert.Contains("Find the network edge", html);
    }

    [Fact]
    public async Task Dismissed_ones_appear_only_in_the_dismissed_view_which_also_offers_Restore_all()
    {
        await PostAsync(Item("192.0.2.1", "Gateway"), Item("192.0.2.50", "LocalDevice"));
        await _client.PostAsync($"/api/v1/discoveries/{await IdOfAsync("192.0.2.50")}/dismiss", null);

        var main = await _client.GetStringAsync("/Config/Discoveries");
        var dismissed = await _client.GetStringAsync("/Config/Discoveries?status=dismissed");

        Assert.DoesNotContain("192.0.2.50", main);
        Assert.DoesNotContain("Restore all dismissed", main);
        Assert.Contains("192.0.2.50", dismissed);
        Assert.DoesNotContain("192.0.2.1<", dismissed);
        Assert.Contains("Restore all dismissed", dismissed);
        Assert.Contains("Restore", dismissed);
    }

    [Fact]
    public async Task An_unknown_status_falls_back_to_the_default_view()
    {
        await PostAsync(Item("192.0.2.1", "Gateway"));
        Assert.Contains("192.0.2.1", await _client.GetStringAsync("/Config/Discoveries?status=bogus"));
    }

    [Fact]
    public async Task Adopt_links_prefill_the_Targets_dialog_by_kind()
    {
        await PostAsync(Item("192.0.2.1", "Gateway", "DefaultRoute"),
            Item("203.0.113.5", "NetworkEdge", "Traceroute"),
            Item("192.0.2.50", "LocalDevice", hostname: "nas.example.com"));

        var html = System.Net.WebUtility.HtmlDecode(await _client.GetStringAsync("/Config/Discoveries"));

        Assert.Contains($"/Config/Targets?address=192.0.2.1&name=Router&profile=Internal&scope=Verdict&discoveryId={await IdOfAsync("192.0.2.1")}", html);
        Assert.Contains($"address=203.0.113.5&name=ISP%20edge&profile=Path&scope=Verdict&discoveryId={await IdOfAsync("203.0.113.5")}", html);
        Assert.Contains($"address=192.0.2.50&name=nas.example.com&profile=Local&scope=Local&discoveryId={await IdOfAsync("192.0.2.50")}", html);
    }

    [Fact]
    public async Task The_suggested_name_is_made_unique_against_existing_targets()
    {
        await ConfigApiTests.SeedHomeAsync(_client);          // already has a target named Router
        await PostAsync(Item("192.0.2.1", "Gateway", "DefaultRoute"));

        var html = System.Net.WebUtility.HtmlDecode(await _client.GetStringAsync("/Config/Discoveries"));

        Assert.Contains("name=Router%202&", html);
    }

    [Fact]
    public async Task An_adopted_discovery_names_its_target_and_a_removed_target_reads_target_removed()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await PostAsync(Item("192.0.2.50", "LocalDevice"));
        var id = await IdOfAsync("192.0.2.50");
        var created = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={id}",
            new { name = "NasBox", address = "192.0.2.50", profile = "Fast", scope = "Local" });
        var targetId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync()).GetProperty("id").GetString();

        var adopted = await _client.GetStringAsync("/Config/Discoveries?status=adopted");
        Assert.Contains("NasBox", adopted);
        Assert.DoesNotContain("target removed", adopted);

        (await _client.DeleteAsync($"/api/v1/targets/{targetId}")).EnsureSuccessStatusCode();
        var removed = await _client.GetStringAsync("/Config/Discoveries?status=adopted");
        Assert.Contains("target removed", removed);
    }

    [Fact]
    public async Task The_Config_tabs_link_to_Discoveries_and_mark_it_active_on_its_own_page()
    {
        var targets = await _client.GetStringAsync("/Config/Targets");
        Assert.Contains("href=\"/Config/Discoveries\"", targets);
        var page = await _client.GetStringAsync("/Config/Discoveries");
        Assert.Contains("class=\"active\" href=\"/Config/Discoveries\"", page);
    }

    [Fact]
    public async Task The_Targets_page_carries_a_valid_discoveryId_into_its_script_and_ignores_junk()
    {
        var id = Guid.NewGuid();
        var withId = await _client.GetStringAsync($"/Config/Targets?address=192.0.2.50&name=Cam&scope=Local&discoveryId={id}");
        Assert.Contains($"const adoptDiscoveryId = \"{id}\";", withId);

        var junk = await _client.GetStringAsync("/Config/Targets?address=192.0.2.50&discoveryId=not-a-guid");
        Assert.Contains("const adoptDiscoveryId = null;", junk);
        var none = await _client.GetStringAsync("/Config/Targets");
        Assert.Contains("const adoptDiscoveryId = null;", none);
    }

    [Fact]
    public async Task The_page_is_locked_behind_the_admin_gate()
    {
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await anon.GetAsync("/Config/Discoveries");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?admin=1", response.Headers.Location?.OriginalString);
    }
}
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~DiscoveriesPageTests"`
Expected: FAIL (404 for the page, no tab, no `adoptDiscoveryId`).

- [ ] **Step 2: Tab and page model**

`_ConfigTabs.cshtml` — add after the Profiles link:

```cshtml
    <a class="@(Model == "discoveries" ? "active" : "")" href="/Config/Discoveries">Discoveries</a>
```

Create `Programs/ConWatch/Pages/Config/Discoveries.cshtml.cs`:

```csharp
using ConCore.Discovery;
using ConPersistence.Config;
using ConPersistence.Discoveries;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>Admin-locked by convention (AdminLockPageFilter on /Config).</summary>
public class DiscoveriesModel(DiscoveryStore store, ConfigStore configStore) : PageModel
{
    public string StatusFilter { get; private set; } = "new";
    public List<DiscoveryDto> Items { get; private set; } = [];
    private HashSet<string> _targetNames = new(StringComparer.OrdinalIgnoreCase);

    public async Task OnGetAsync(string? status)
    {
        if (!DiscoveryStatuses.TryParseFilter(status, out var filter))
        {
            status = null;
            DiscoveryStatuses.TryParseFilter(null, out filter);
        }
        StatusFilter = string.IsNullOrWhiteSpace(status) ? "new" : status.Trim().ToLowerInvariant();
        Items = await store.ListAsync(filter);
        _targetNames = (await configStore.ExportAsync()).Targets
            .Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string KindLabel(DiscoveryKind kind) => kind switch
    {
        DiscoveryKind.Gateway => "Router (gateway)",
        DiscoveryKind.NetworkEdge => "Network edge (first hop beyond your LAN)",
        _ => "Local device",
    };

    public static string MethodLabel(DiscoveryMethod method) => method switch
    {
        DiscoveryMethod.DefaultRoute => "default route",
        DiscoveryMethod.CommonAddress => "common router address",
        DiscoveryMethod.Traceroute => "traceroute",
        DiscoveryMethod.ArpTable => "ARP table",
        _ => "ping sweep",
    };

    private string UniqueName(string wanted)
    {
        wanted = wanted.Trim();
        if (wanted.Length > 64)
            wanted = wanted[..64];
        if (!_targetNames.Contains(wanted))
            return wanted;
        for (var n = 2; ; n++)
            if (!_targetNames.Contains($"{wanted} {n}"))
                return $"{wanted} {n}";
    }

    /// <summary>The Targets dialog, pre-filled for this discovery; saving it adopts the discovery.</summary>
    public string AdoptUrl(DiscoveryDto d)
    {
        var (name, profile, scope) = d.Kind switch
        {
            DiscoveryKind.Gateway => (UniqueName("Router"), "Internal", "Verdict"),
            DiscoveryKind.NetworkEdge => (UniqueName("ISP edge"), "Path", "Verdict"),
            _ => (UniqueName(string.IsNullOrWhiteSpace(d.Hostname) ? d.Address : d.Hostname), "Local", "Local"),
        };
        var query = new QueryString()
            .Add("address", d.Address).Add("name", name).Add("profile", profile)
            .Add("scope", scope).Add("discoveryId", d.Id.ToString());
        return "/Config/Targets" + query;
    }
}
```

- [ ] **Step 3: The page**

Create `Programs/ConWatch/Pages/Config/Discoveries.cshtml`:

```cshtml
@page
@using ConCore.Discovery
@model ConWatch.Pages.Config.DiscoveriesModel
@{
    ViewData["Title"] = "Discoveries — ConnectionWatcher";
}

<div class="cw-pageband">
    <div class="titles">
        <h2>Discoveries</h2>
        <p>Things found on the network that nobody is monitoring yet. Adopt one to turn it into a target, or dismiss it.</p>
    </div>
</div>
<partial name="_ConfigTabs" model="@("discoveries")" />

<p>
    <button type="button" id="find-router">Find my router</button>
    <button type="button" id="find-edge" class="outline">Find the network edge</button>
    <span id="search-status" aria-live="polite"></span>
</p>
<ul class="form-errors" id="errors"></ul>

<div class="cw-segmented" style="margin-bottom: 1rem">
    @foreach (var (key, label) in new[] { ("new", "New"), ("dismissed", "Dismissed"), ("adopted", "Adopted"), ("all", "All") })
    {
        <a class="@(Model.StatusFilter == key ? "active" : "")" href="/Config/Discoveries?status=@key">@label</a>
    }
</div>

@if (Model.StatusFilter == "dismissed" && Model.Items.Count > 0)
{
    <p><button type="button" class="outline" id="restore-all">Restore all dismissed</button></p>
}

@if (Model.Items.Count == 0)
{
    <p>Nothing here. Use the search buttons above.</p>
}
else
{
    <table>
        <thead><tr><th>Address</th><th>What</th><th>How found</th><th>Evidence</th><th>First seen</th><th>Last seen</th><th></th></tr></thead>
        <tbody>
        @foreach (var d in Model.Items)
        {
            <tr>
                <td>@d.Address@if (!string.IsNullOrEmpty(d.Hostname)) { <br /><small>@d.Hostname</small> }</td>
                <td>@ConWatch.Pages.Config.DiscoveriesModel.KindLabel(d.Kind)</td>
                <td>@ConWatch.Pages.Config.DiscoveriesModel.MethodLabel(d.Method)<br /><small>@d.Origin</small></td>
                <td><small>@d.Evidence</small></td>
                <td><small data-utc="@d.FirstSeenUtc.ToString("o")">@d.FirstSeenUtc.ToString("u")</small></td>
                <td><small data-utc="@d.LastSeenUtc.ToString("o")">@d.LastSeenUtc.ToString("u")</small></td>
                <td>
                    @if (d.Status == DiscoveryStatus.Adopted && d.AdoptedTargetName is { } adopted)
                    {
                        <span>→ <a href="/Config/Targets">@adopted</a></span>
                    }
                    else
                    {
                        @if (d.Status == DiscoveryStatus.Adopted)
                        {
                            <small>(target removed)</small>
                        }
                        <a role="button" class="outline" href="@Model.AdoptUrl(d)">Adopt</a>
                        @if (d.Status == DiscoveryStatus.Dismissed)
                        {
                            <button type="button" class="outline" data-action="restore" data-id="@d.Id">Restore</button>
                        }
                        else
                        {
                            <button type="button" class="outline" data-action="dismiss" data-id="@d.Id">Dismiss</button>
                        }
                    }
                </td>
            </tr>
        }
        </tbody>
    </table>
}

@section Scripts {
    <script src="~/lib/site-config.js" asp-append-version="true"></script>
    <script>
        const errors = document.getElementById("errors");
        const statusLine = document.getElementById("search-status");

        document.querySelectorAll("[data-utc]").forEach((el) => {
            const when = new Date(el.dataset.utc);
            if (!isNaN(when)) el.textContent = when.toLocaleString();
        });

        async function search(button, url, label) {
            clearErrors(errors);
            button.setAttribute("aria-busy", "true");
            statusLine.textContent = `${label}…`;
            const result = await apiFetch("POST", url, {});
            button.removeAttribute("aria-busy");
            if (!result.ok) {
                statusLine.textContent = "";
                showErrors(errors, result);
                return;
            }
            const { found, created, warning } = result.body;
            statusLine.textContent = found === 0
                ? "Nothing found." + (warning ? " " + warning : "")
                : `Found ${found} (${created} new).` + (warning ? " " + warning : "");
            if (found > 0) setTimeout(() => location.reload(), warning ? 4000 : 800);
        }

        document.getElementById("find-router").addEventListener("click",
            (e) => search(e.currentTarget, "/api/v1/discoveries/search/router", "Looking for your router"));
        document.getElementById("find-edge").addEventListener("click",
            (e) => search(e.currentTarget, "/api/v1/discoveries/search/edge", "Tracing the way out of your network"));

        document.querySelectorAll("[data-action]").forEach((btn) =>
            btn.addEventListener("click", async () => {
                clearErrors(errors);
                const result = await apiFetch("POST", `/api/v1/discoveries/${btn.dataset.id}/${btn.dataset.action}`);
                if (result.ok) location.reload();
                else showErrors(errors, result);
            }));

        document.getElementById("restore-all")?.addEventListener("click", async () => {
            clearErrors(errors);
            const result = await apiFetch("POST", "/api/v1/discoveries/restore-dismissed");
            if (result.ok) location.href = "/Config/Discoveries";
            else showErrors(errors, result);
        });
    </script>
}
```

(If `apiFetch` returns the parsed body under a different property than `.body`, use the property the existing pages read — e.g. `Targets.cshtml`'s probe test reads `r.body`.)

- [ ] **Step 4: Carry the discovery id through the Targets dialog**

`Targets.cshtml.cs` — add beside the other prefill properties:

```csharp
    /// <summary>The discovery being adopted (from the Discoveries page); null when absent or malformed.</summary>
    public string? PrefillDiscoveryId =>
        Guid.TryParse(Request.Query["discoveryId"], out var id) ? id.ToString() : null;
```

`Targets.cshtml` — in the script, next to the other page-level constants (right after `const pageDefaultProfile = …;`), add the immutable page value and a mutable working copy the dialog can clear:

```js
        const adoptDiscoveryId = @Html.Raw(json(Model.PrefillDiscoveryId));
        let adoptingId = adoptDiscoveryId;
```

In `beforeSave`, immediately after the `insertChildIds` block (which ends with `return false; }`) and before `const original = form.dataset.originalAddress;`, add:

```js
                if (!isEdit && adoptingId) {
                    // Strip the prefill from the URL first so the reload after a successful
                    // adoption does not re-open the dialog; a failed save keeps adoptingId for the retry.
                    history.replaceState(null, "", location.pathname);
                    return { query: `?discoveryId=${encodeURIComponent(adoptingId)}` };
                }
```

and in `resetForm` (which `wireConfigPage` calls whenever the dialog closes), add as its first line `adoptingId = null;` — so cancelling the prefilled dialog and then adding an unrelated target never adopts by accident.

The page test asserts the literal text `const adoptDiscoveryId = "<guid>";` / `const adoptDiscoveryId = null;`, which the `const` line above produces (`json(string)` emits a quoted string, `json(null)` emits `null`).

- [ ] **Step 5: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug`
Expected: all PASS (including the existing Targets-page smoke tests). The dialog's runtime behaviour (adopt hand-off, reset on cancel, URL strip) has no automated test; Task 7's browser check exercises it.

```bash
git add Programs Tests
git commit -m "feat(discoveries): Config page with search, dismiss, restore and adopt hand-off to the Targets dialog"
```

---

### Task 6: MCP `list_discoveries` (read-only)

**Files:**
- Modify: `Programs/ConWatch/Mcp/ConWatchMcpTools.cs`
- Test: modify `Tests/ConWatch.Tests/McpEndpointTests.cs`

**Interfaces:**
- Consumes: `DiscoveryStore`, `DiscoveryStatuses.TryParseFilter` (Task 1).
- Produces: MCP tool `list_discoveries(status?)` — `status` is `new` (default), `dismissed`, `adopted` or `all`; returns the `DiscoveryDto` array as JSON; a bad status is a readable tool error (`McpException`, because the SDK hides every other exception's message). Strictly read-only: no search, adopt, dismiss or restore tool exists.

- [ ] **Step 1: Write the failing test**

`McpEndpointTests` already has `RpcAsync`, `InitializeAsync` and `ToolText(callResult)` (the tool's string output is `content[0].text`; `isError` sits on the call result). Add to it:

```csharp
    private async Task<List<JsonElement>> ListDiscoveriesAsync(object arguments)
    {
        var text = ToolText(await RpcAsync("tools/call", new { name = "list_discoveries", arguments }));
        return JsonSerializer.Deserialize<JsonElement>(text).EnumerateArray().ToList();
    }

    [Fact]
    public async Task list_discoveries_returns_the_inbox_and_filters_by_status()
    {
        await InitializeAsync();
        (await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new object[]
            {
                new { address = "192.0.2.1", kind = "Gateway", method = "DefaultRoute", origin = "server", evidence = "e1" },
                new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" },
            },
        })).EnsureSuccessStatusCode();
        var all = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/discoveries"));
        var dismissId = all.EnumerateArray().Single(d => d.GetProperty("address").GetString() == "192.0.2.50")
            .GetProperty("id").GetString();
        (await _client.PostAsync($"/api/v1/discoveries/{dismissId}/dismiss", null)).EnsureSuccessStatusCode();

        var newOnes = await ListDiscoveriesAsync(new { });
        var dismissed = await ListDiscoveriesAsync(new { status = "dismissed" });
        var everything = await ListDiscoveriesAsync(new { status = "all" });

        Assert.Equal(["192.0.2.1"], newOnes.Select(d => d.GetProperty("address").GetString()));
        Assert.Equal("Gateway", newOnes[0].GetProperty("kind").GetString());
        Assert.Equal("server", newOnes[0].GetProperty("origin").GetString());
        Assert.Equal(["192.0.2.50"], dismissed.Select(d => d.GetProperty("address").GetString()));
        Assert.Equal(2, everything.Count);
    }

    [Fact]
    public async Task list_discoveries_with_a_bad_status_is_a_readable_tool_error()
    {
        await InitializeAsync();

        var result = await RpcAsync("tools/call", new { name = "list_discoveries", arguments = new { status = "bogus" } });

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("status must be", ToolText(result));
    }
```

Also update the existing test `Lists_the_four_read_only_tools` (line ~63): rename it `Lists_the_five_read_only_tools`, change `Assert.Equal(4, tools.Count);` to `Assert.Equal(5, tools.Count);` and add `Assert.Contains("list_discoveries", tools);`. That test is the guard that no write tool slips in — it must still pass with exactly these five.

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~McpEndpointTests"`
Expected: FAIL (unknown tool; the tool-list assertion).

- [ ] **Step 2: Implement**

`ConWatchMcpTools.cs` — add `using ConPersistence.Discoveries;` and extend the primary constructor:

```csharp
public class ConWatchMcpTools(WatcherHostService host,
    IDbContextFactory<ConWatchDbContext> dbFactory, IspReportBuilder reportBuilder,
    DiscoveryStore discoveries)
```

and add the tool at the end of the class:

```csharp
    [McpServerTool(Name = "list_discoveries", ReadOnly = true)]
    [Description("Candidates found by network searches that nobody is monitoring yet: the user's " +
        "router, the first hop beyond their LAN (where the ISP's network begins), and local devices. " +
        "Each has a kind (Gateway, NetworkEdge, LocalDevice), how it was found (method), who found it " +
        "(origin: 'server' or 'tray:<machine>'), evidence, first/last seen (ISO-8601 UTC) and a status: " +
        "New (not acted on), Dismissed (the user said no), Adopted (turned into a target; adoptedTargetName " +
        "is null if that target was later deleted). Read-only: searching, adopting and dismissing are done " +
        "in the web UI's Config > Discoveries page.")]
    public async Task<string> ListDiscoveries(
        [Description("'new' (default), 'dismissed', 'adopted' or 'all'")] string? status = null)
    {
        if (!DiscoveryStatuses.TryParseFilter(status, out var filter))
            throw new McpException("status must be 'new', 'dismissed', 'adopted' or 'all'.");
        return JsonSerializer.Serialize(await discoveries.ListAsync(filter), Json);
    }
```

(`McpException` is in the `ModelContextProtocol` namespace, already imported by plan 1B's change to this file.)

- [ ] **Step 3: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug`
Expected: all PASS.

```bash
git add Programs Tests
git commit -m "feat(mcp): read-only list_discoveries tool"
```

---

### Task 7: Whole-branch verification and browser check

**Files:** none (verification only; fix any defect in the owning task's files).

- [ ] **Step 1: Run every automated check**

```bash
dotnet test Tests/ConEngine.Tests -c Debug
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug
dotnet test Tests/ConConfig.Tests -c Debug
dotnet test Tests/ConWatchCli.Tests -c Debug
dotnet test Tests/ConNotify.Tests -c Debug
cd Programs/ConWatch/ClientApp && npx tsc --noEmit && npx vitest run && npm run build
```

Expected: all PASS (ConWatch known flake aside). Baselines before this plan: ConEngine 225, ConPersistence 146, ConWatch 259, ConConfig 36, ConWatchCli 64, ConNotify 32, vitest 69.

- [ ] **Step 2: Browser check against a scratch instance**

Build the client first. Run the worktree's own Release build on a free loopback port with a SCRATCH database (e.g. `dotnet run --project Programs/ConWatch -c Release -- --urls http://localhost:18099 --Persistence:DbPath <scratch>/conwatch.db`); **do not** use `preview_start conwatch-preview` — it launches the main checkout's binary, not the worktree's. Never touch the user's real ConnectionWatcher service, tray or data directory; the product may be installed and running on its own port. Admin password unset. Seed through the REST API (profile `Fast`, a router `Router` at 127.0.0.1, an anchor pool with two documentation-address anchors, `Verdict.AnchorPool=Internet`), then with the built-in browser (`mcp__Claude_Browser__*`) verify and record what you observe:

1. `/Config/Targets` shows a **Discoveries** tab; clicking it opens `/Config/Discoveries` with the tab highlighted and the empty state "Nothing here."
2. **Find my router** runs (a real search of this machine's default gateway): the status line reports found/new counts (it may find 0 on a machine with no default route — then say so), and after the reload at least the default-route candidate appears as a New row with kind "Router (gateway)", method "default route" and origin "server". Do **not** put the real router address in any committed file or in the final report beyond "a private address".
3. **Find the network edge** runs a traceroute toward 1.1.1.1 and stops at the first public hop: a NetworkEdge row (or "Nothing found." with a clear message) appears within ~30 s and the button is usable again afterwards. Click it twice quickly: the second click must show the "another search is already running" error, not hang.
4. Post a batch via the API (`POST /api/v1/discoveries`, a `LocalDevice` at 192.0.2.50 with hostname `nas.example.com`), reload, then: **Dismiss** it → it leaves the New view, shows under **Dismissed** with a **Restore** button and **Restore all dismissed**; **Restore** puts it back.
5. **Adopt** the LocalDevice: the Targets dialog opens right away with address 192.0.2.50, name `nas.example.com`, scope **LAN device**, Parent disabled, profile `Local`; **cancel** the dialog, add an unrelated target by hand (e.g. `Other` at 192.0.2.77) and confirm it did **not** adopt the discovery (the discovery is still New). Then Adopt again and **save**: the page reloads on a clean `/Config/Targets` URL (no `?address=` prefill, dialog closed), the target exists with scope Local, and the Discoveries page shows the row under **Adopted** with "→ nas.example.com". Delete that target and confirm the Adopted row now reads "(target removed)" with Adopt and Dismiss available.
6. Adopt the gateway candidate while a target named `Router` exists: the suggested name is `Router 2`.
7. MCP: `POST /mcp` `tools/call list_discoveries` (follow the JSON-RPC pattern in `McpEndpointTests`) returns the inbox as JSON; the tool list contains no search/adopt/dismiss tool.
8. With an admin password set and logged out, `/Config/Discoveries` redirects to `/?admin=1` and `GET /api/v1/discoveries` is 401.

Stop the app and delete the scratch data. Record what you observed (and anything that did not match, with reproduction steps); anything that does not match is a defect for the owning task.

- [ ] **Step 3: Commit only if something was fixed**

```bash
git status --short
git add -A -- Core Libraries Programs Tests
git commit -m "fix: address verification findings for the Discoveries inbox"
```

(Skip if clean. Do not push; hand the branch back for review before merging to `master`.)
