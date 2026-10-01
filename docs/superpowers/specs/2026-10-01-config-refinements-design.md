# Config refinements: naming, pools, profiles, traceroute classification

Date: 2026-10-01. Baseline: `feature/config-dialogs` at `2b716c7` (the dialog
editing commit on top of master `60feb47`). Status: approved in chat as five
sections; awaiting review of this file.

## 1. Goal

Four refinements to the configuration side of the web UI, decided together
because they touch the same pages:

1. The top-level nav entry called **Targets** has a sub-tab also called
   Targets. Rename the entry **Config** and the **Admin** page **Settings**.
2. Pools: put the one setting that makes a pool matter (the verdict anchor)
   on the Pools page, and replace the click-to-append member picker with a
   two-list editor that can reorder.
3. Profiles: three built-ins instead of two (**Internal**, **Path**,
   **Public**), each with a user-editable description, and rename the
   existing **External** to **Public** everywhere, including stored data.
4. Traceroute: classify each hop as Internal / Path / Public so "Add as
   target" pre-selects the profile, and, when the user turns it on, look up
   each public hop's autonomous system so the operator field can be
   pre-filled.

Out of scope: the API paths under `/api/v1/admin` keep their names; the
"admin password" keeps its name; the stagger-slot collision for a target in
two pools (noted in the backlog, §8); any change to how the verdict works.

## 2. Names and navigation

### 2.1 Top bar and drawer

`_Layout.cshtml` `manageNav` becomes:

| Key | Href | Label | Active prefix |
|---|---|---|---|
| data | /Data | Data | /Data |
| config | /Config/Targets | Config | /Config |
| traceroute | /Traceroute | Traceroute | /Traceroute |
| settings | /Settings | Settings | /Settings |

Icons are unchanged (the "targets" icon is reused for Config, the "admin"
icon for Settings). `_ConfigTabs.cshtml` keeps Targets / Pools / Profiles.

### 2.2 Admin page becomes Settings

- `Pages/Admin.cshtml` + `.cs` move to `Pages/Settings.cshtml` + `.cs`
  (`SettingsModel`). Page band: title **Settings**, subtitle "Node controls:
  monitoring, notifications, retention, and the danger zone."
- `/Admin` stays routable as a permanent redirect to `/Settings`
  (`app.MapGet("/Admin", () => Results.Redirect("/Settings", permanent: true))`)
  so bookmarks and older README links keep working.
- `ConWatchServices.cs:143` adds `"/Settings"` to the admin-locked page list
  (`/Admin` can stay in the list; the redirect fires before the page filter
  would, but leaving it is harmless).
- `site-admin.js` is only loaded by this page; it is renamed
  `site-settings.js` with the `<script>` reference moving along.
- README: every "Admin page" becomes "Settings page". The README's `/Admin`
  deep links, if any, become `/Settings`.
- The MCP server and the CLI do not link to the page; nothing else to change.

### 2.3 Verdict section moves to Pools

The "Verdict anchor pool" select and the "anchors needed for Healthy"
(`Verdict.WeatherThreshold`) input leave the Settings page. See §3.1 for
where they land. `SettingsEndpoints` keeps validating both keys; only the
page that posts them changes.

## 3. Pools page

### 3.1 Verdict section

Above the table, a section **Verdict** with one sentence of explanation
("The verdict reads the members of one pool, the internet anchors. Other
pools only share pinging load and report a combined state.") and the two
controls:

- **Anchor pool**: a select over the pools plus "(none, verdict off)".
- **Anchors needed for Healthy**: the existing integer input.
- A **Save** button posting both through the existing settings API
  (`PUT /api/v1/settings`), same JS as the Settings page uses today.

The table gains a column that shows **anchor** (a `cw-chip`) on the row of
the pool named by `Verdict.AnchorPool`, matched case-insensitively as the
engine does.

`PoolsModel` gains `AnchorPool` (string?) and `WeatherThreshold` (int) read
from `SettingsStore`, the same way `AdminModel` reads them today.

### 3.2 Two-list member editor

Inside the pool dialog, the `Members` fieldset becomes:

```
Members (in stagger order)
┌ Available ─────────┐          ┌ Members ───────────┐
│ Google DNS         │  [Add →] │ Quad9 DNS          │
│ Router             │ [← Remove]│ Cloudflare        │
│ …                  │          │ …                  │
└────────────────────┘  [↑] [↓] └────────────────────┘
```

- Both lists are `<select multiple size="6">`. Available is every enabled
  or disabled target not already a member, sorted by name. Members is in
  stagger order.
- **Add** moves the selected entries of Available to the end of Members.
  **Remove** moves the selected entries of Members back to Available (which
  re-sorts). **Up** / **Down** move the selected Members entry one step and
  are disabled when nothing is selected or it is already at that end.
  Double-clicking an entry in either list does the same as Add / Remove.
- The `members` array in the page script remains the source of truth;
  `buildDto` sends it unchanged, `fillForm` and `resetForm` rebuild both
  lists from it.
- Layout: a three-column grid (list, button column, list) at desktop width;
  Pico's `.grid` collapses it to one column under 768 px, with the button
  column rendered as a horizontal row between the two lists. About fifteen
  lines of CSS in `site.css` under a `.cw-duallist` class.
- The hint text under the fieldset: "Member order sets the ping stagger:
  member *k* of *n* pings *k/n* of the interval after member 1."

No server change for this section.

## 4. Profiles

### 4.1 Built-ins

`PingProfile` (engine, `Core/ConCore/Targets/PingProfile.cs`):

| Property | Interval | Doc comment |
|---|---|---|
| `Internal` | 1 s | Preset for LAN targets (unchanged) |
| `Path` | 5 s | Preset for hops on the route to the internet that the user does not own but may probe steadily |
| `Public` | 10 s | Preset for public internet sites: polite steady cadence (was `External`) |

All other numbers stay at the shared defaults. `PingProfile.External` is
removed, not kept as an alias; the compiler finds every reference.
`TargetGraph.cs:38-44` registers all three; `WatcherOptionsValidator
.IsKnownProfile` accepts all three; `WatcherOptions.cs:10` and
`TargetConfig.Profile`'s default say `Public`; `TargetDto.Profile` default
is `Public`; `Targets.cshtml` selects `Public` by default.

Descriptions do not live on `PingProfile` (the engine never shows them).
They live in persistence as `BuiltInProfiles` in `ConPersistence/Config`:

```csharp
public static class BuiltInProfiles
{
    public static readonly IReadOnlyList<(PingProfile Profile, string Description)> All =
    [
        (PingProfile.Internal, "Devices on your own LAN. Probe as hard as you like."),
        (PingProfile.Path, "Hops on your route to the internet that you don't own but may " +
                           "probe steadily, such as the ISP gateway."),
        (PingProfile.Public, "Public internet sites. Be polite."),
    ];
    public static bool IsBuiltIn(string name) => …; // case-insensitive
}
```

`ConfigStore.SeedAsync` seeds from `BuiltInProfiles.All` and skips
appsettings profiles whose names collide with any built-in (today it checks
two literals). `AdminEndpoints` reset re-seeds through the same path.

### 4.2 Description column

- `ProfileRow.Description` (`string?`), `ProfileDto.Description`
  (`string?`), round-tripped by `ConfigService.UpsertProfileAsync`, export,
  import, and the `/api/v1/profiles` endpoints. Validation: trimmed, at most
  500 characters, empty becomes null.
- Profiles page: a **Description** column after Name (muted, wraps). The
  dialog gets a `<textarea name="description" rows="2">` under Name.
- The Targets dialog's Profile select shows the description as the option's
  `title` so hovering explains the choice. (A richer picker is not worth it
  for three options.)

### 4.3 Migration: `AddProfileDescription`

One EF Core migration under `Libraries/ConPersistence/Migrations`:

1. `AddColumn<string>("Description", "Profiles", nullable: true)`.
2. `migrationBuilder.Sql(...)`, in order:
   - `UPDATE Profiles SET Name = 'Public' WHERE Name = 'External' COLLATE NOCASE
      AND NOT EXISTS (SELECT 1 FROM Profiles WHERE Name = 'Public' COLLATE NOCASE);`
   - Targets reference profiles by `ProfileId`, so the rename above carries
     them with it. No target update is needed. (The `Targets` table has no
     profile-name column; confirmed in `ConfigRows.cs`.)
3. `Down` drops the column and leaves the name alone.

This is ordinary migration history: on a fresh database the `UPDATE`
matches no rows. Nothing to track or remove before going public.

The edge case in the guard (a user already has a custom profile named
Public) leaves their External in place; the startup step below then adds
nothing named Public because one exists, and their External keeps working
as a custom profile. Acceptable.

### 4.4 Startup: ensure built-ins

`ConfigStore.EnsureBuiltInsAsync()` runs after `Migrate()` on every start:
for each entry in `BuiltInProfiles.All` with no row of that name
(case-insensitive), insert it; for each built-in row whose `Description` is
null, fill the built-in description. It never overwrites numbers or a
non-null description. This is what gives an existing database its `Path`
profile, and it is legitimate steady-state code (it also heals a database
where someone deleted a built-in), so it stays after going public.

### 4.5 Setup wizard and copy

`SetupService.cs`: Router → `Internal`, WAN hop → `Path`, anchors →
`Public`. `Setup.cshtml:65` "polite External cadence" → "polite Public
cadence". Any other UI copy naming External follows.

### 4.6 Config import of older exports

An export taken before this change names `External`. Import validates
profile names against the stored profiles and will reject it. No shim; the
user re-exports after upgrading. The README's backup paragraph gets one
sentence saying exports are tied to the version that produced them.

## 5. Traceroute classification and ASN lookup

### 5.1 Hop record

`TracerouteHop` gains three fields:

```csharp
public enum HopKind { Internal, Path, Destination }
public record TracerouteHop(int Ttl, string? Address, string? Hostname, long? RttMs,
    bool ReachedDestination, HopKind? Kind, int? Asn, string? AsName);
```

`Kind` is null for a hop that did not answer.

### 5.2 Classifier

`HopClassifier.Classify(IReadOnlyList<TracerouteHop> hops)` in
`ConInfrastructure/Traceroute`, a pure function run by `TracerouteRunner`
after the walk:

- A hop whose address is private (RFC 1918), link-local, loopback, or CGNAT
  (100.64.0.0/10) **and** that comes before the first public hop is
  `Internal`.
- Every other answering hop before the destination is `Path`. (This
  includes a private or CGNAT hop that appears after a public one; such a
  hop is inside some carrier, not on the user's LAN.)
- The hop with `ReachedDestination` is `Destination`.

`ProbePolicy.IsPrivate` (`Core/ConCore/ProbeSpec.cs`) gains the CGNAT range
behind a new `IsPrivateOrCarrier(string)` so the TCP port policy is
untouched; the classifier uses the new helper.

The mapping to profiles happens in the page script: `Internal` → profile
Internal, `Path` → Path, `Destination` → Public.

### 5.3 ASN lookup

New in `ConInfrastructure/Asn`:

```csharp
public record AsnInfo(int Asn, string Name, string? CountryCode);

public interface IAsnLookup
{
    /// Null when the address is private/carrier, when the lookup is off,
    /// when the resolver does not answer in time, or when the answer does
    /// not parse. Never throws for a bad address.
    Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct);
}
```

`CymruAsnLookup : IAsnLookup` uses the Team Cymru IP-to-ASN DNS service:

1. TXT query for `<d>.<c>.<b>.<a>.origin.asn.cymru.com`, answer like
   `"7922 | 73.0.0.0/8 | US | arin | 2005-01-27"`. Take the first ASN when
   several are listed (`"7922 33667 | …"`).
2. TXT query for `AS7922.asn.cymru.com`, answer like
   `"7922 | US | arin | 2005-01-27 | COMCAST-7922, US"`. The AS name is the
   last field with the trailing `, CC` removed.
3. `CymruResponseParser` holds the two parse functions as pure statics so
   they can be unit-tested without DNS.
4. Timeout 2 s per query; the two queries run in sequence. Results are
   cached in a `ConcurrentDictionary<IPAddress, (AsnInfo?, DateTime)>` for
   24 hours; failures are cached for 10 minutes so a traceroute re-run does
   not hammer the resolver.
5. DNS client: the `DnsClient` NuGet package (MIT), the first dependency in
   `ConInfrastructure.csproj` beyond logging abstractions. The built-in
   `System.Net.Dns` cannot issue TXT queries. The lookup uses the system
   resolver (`LookupClient` default), so no new outbound host is contacted
   directly; the system resolver fetches the records from Cymru's name
   servers.

`TracerouteRunner` takes an `IAsnLookup` and a `bool asnEnabled` per run
(passed by the endpoint from the setting). After classification it looks up
every `Path` and `Destination` hop in parallel (at most 8 at a time) and
fills `Asn` / `AsName`. A `NullAsnLookup` keeps the existing tests and the
CLI unchanged.

### 5.4 Setting

`SettingKeys.TracerouteAsnLookup = "Traceroute.AsnLookup"`, bool, default
`false`. The Settings page gets a **Traceroute** section with one checkbox:

> **Look up the operator of each hop**. Sends the address of every public
> hop to Team Cymru's IP-to-ASN service through your DNS resolver. Off by
> default. Private addresses are never sent.

### 5.5 Traceroute page

- The table gains **Kind** (Internal / Path / Destination, as a muted
  label) and, when the setting is on, **Operator** (the AS name, e.g.
  `COMCAST-7922`; blank when unknown). The response carries
  `asnLookupEnabled: bool` so the page can decide whether to render the
  column.
- "Add as target" passes `profile=<Internal|Path|Public>` and, when
  present, `operator=<AsName>` in the query string. `TargetsModel` gains
  `PrefillProfile` and `PrefillOperator`; the dialog pre-selects the profile
  option (falling back to Public if the named profile does not exist) and
  fills the operator input.
- The "already monitored as" cell is unchanged.

### 5.6 Privacy and failure behaviour

- With the setting off, no lookup runs and the response says so; the page
  shows no Operator column.
- A resolver that does not answer slows the traceroute by at most 2 s per
  unanswered query, bounded by the parallelism cap and the failure cache.
  The traceroute result is never withheld because a lookup failed.

## 6. Testing

Unit tests, all offline:

- `HopClassifierTests` (ConEngine.Tests, beside `TracerouteRunnerTests`):
  LAN → public → destination; CGNAT first hop is Internal; a private hop
  after a public one is Path; unanswered hops get null; a destination that
  is itself private (tracing to a LAN host) is Destination.
- `CymruResponseParserTests`: the two sample answers above, a multi-ASN
  origin answer, an empty answer, a malformed answer → null.
- `TracerouteRunnerTests`: existing tests pass with `NullAsnLookup`; one new
  test with a fake `IAsnLookup` checks that only Path/Destination hops are
  looked up and that the fields land on the right hops.
- `ConfigStoreTests` (ConPersistence.Tests): a database seeded with
  Internal + External and a target on External, migrated, ends with
  Internal + Path + Public, the target on Public, and all three built-ins
  carrying descriptions. A second test: a custom profile named Public
  already present leaves External untouched.
- `ConfigApiTests` (ConWatch.Tests): description round-trips through
  POST/PUT/GET and export/import; a 501-character description is rejected.
- `SetupServiceTests`: the wizard's profile assignments.
- `SettingsEndpoints`: `Traceroute.AsnLookup` joins the existing bool
  cases (`MqttEnabled`, `WebhookEnabled`, …) and accepts `true`/`false` only.

Browser verification in the preview app, as for the dialog work: nav
labels and the `/Admin` redirect; the Verdict section saves and the badge
moves; the member editor at desktop and phone width (add, remove, reorder,
cancel resets); the profile description in table and dialog; a traceroute
with the lookup off and on, and "Add as target" landing in the Targets
dialog with profile and operator filled.

## 7. Files touched (summary)

- Core: `PingProfile.cs`, `TargetGraph.cs`, `WatcherOptions.cs`,
  `WatcherOptionsValidator.cs`, `ProbeSpec.cs`.
- ConInfrastructure: `Traceroute/TracerouteRunner.cs`,
  `Traceroute/HopClassifier.cs` (new), `Asn/*` (new), csproj.
- ConPersistence: `Entities/ConfigRows.cs`, `Config/ConfigDtos.cs`,
  `Config/ConfigService.cs`, `Config/ConfigStore.cs`,
  `Config/BuiltInProfiles.cs` (new), `Settings/SettingKeys.cs`,
  `Migrations/<stamp>_AddProfileDescription.cs` (new) + snapshot.
- ConWatch: `Pages/_Layout.cshtml`, `Pages/Settings.cshtml(.cs)` (renamed
  from Admin), `Pages/Config/Pools.cshtml(.cs)`, `Pages/Config/Profiles
  .cshtml`, `Pages/Config/Targets.cshtml(.cs)`, `Pages/Traceroute.cshtml`,
  `Pages/Setup.cshtml`, `Api/SettingsEndpoints.cs`,
  `Api/TracerouteEndpoints.cs`, `SetupService.cs`,
  `Startup/ConWatchServices.cs` (filter list, redirect, DI for the lookup),
  `wwwroot/lib/site-traceroute.js`, `wwwroot/css/site.css`, README.
- Tests as listed in §6.

## 8. Backlog note

Add to `docs/superpowers/BACKLOG.md`: a target that belongs to two pools
gets one stagger slot, from whichever pool `WatcherEngine.cs:127` visits
last. Either warn in `ConfigHealth` or assign a slot per pool. Found
2026-10-01 while reading `PoolCoordinator`.
