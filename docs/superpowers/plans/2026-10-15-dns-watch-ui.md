# DNS Watch — Plan 3: UI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the name-resolution watch in front of the person: a "Name resolution" section on the dashboard, a second banner line when a link outage also takes DNS down, a DNS config page where resolvers and lookup names are managed, a wizard step that names the upstream resolver, and the DNS-over-HTTPS health warning.

**Architecture:** The dashboard section is a React component beside the LAN section, fed by the `dns` block the status snapshot already carries (Plan 2), with a server-rendered fallback like the LAN one. The config page is a Razor page in the existing `/Config` tab set, server-rendered from the stored config (so disabled resolvers appear) and writing through the existing admin-locked settings and targets endpoints — no new routes. The wizard step posts one new field. DoH detection is an injectable reader of Windows' per-interface `DohInterfaceSettings` registry keys, fail-safe to "not configured".

**Tech Stack:** React + vitest (client), Razor Pages + vanilla JS via `wwwroot/lib/site-config.js` (config pages), .NET 10, xUnit with `ConWatchFactory` host tests.

**Spec:** `docs/superpowers/specs/2026-10-10-dns-watch-design.md` §5.2, §5.3, §4.5 (`dns-over-https`), rollout step 3. Plans 1 and 2 are merged (master 5ca57af). One spec deviation by ruling: §5.1's `/api/v1/config/dns` routes are not added; the page uses the existing admin-locked `PUT /api/v1/settings` and `/api/v1/targets` endpoints (Task 6 records it in the spec).

## Global Constraints

- The `dns` status block (Plan 2): `{ watched, state: "Unknown|Ok|Degraded|Filtered|Down", systemResolvers[], upstreamResolvers[], blame, lookupNames[] }`; resolver entries `{ id, address, label, state, detail, enabled }`; lookup names `{ name, lastResult: "ok|miss|filtered|none", detail }`; `blame` `{ kind: "YourResolver|NoResolver", message, resolvers[] }` or null. A `DnsDown` verdict may arrive with `blame` null (warm start).
- Dashboard section copy (spec §5.2): heading **Name resolution**; one line per resolver: label, a state chip, and the last detail only when the resolver is not Responding; one line per lookup name **only** when that name has a problem (`miss` or `filtered`); when `watched` is false, exactly one line: **Name resolution isn't being watched.** The section never colours the verdict or the tray.
- Summary wording in the heading (ruling for the parked M3): `Ok` → "working"; `Filtered` → "working, but your resolver blocks the check names"; `Degraded` → "one resolver isn't answering"; `Down` with verdict `DnsDown` → "not working"; `Down` with any other verdict → "re-checking"; `Unknown` → "checking".
- Banner (spec §5.2): the `LinkDown` slab gains a second caveat line **Name lookups are failing too** when `dns.state === "Down"`; nothing else about the slab changes.
- Config page (spec §5.3): route `/Config/Dns`, tab label **DNS**, heading **DNS**; an Enabled toggle (settings key `Dns.Enabled`); system resolvers as read-only rows (label, address, enabled) with a per-row enable/disable switch and the caption **found from your network settings**; upstream resolvers with add (address, name) and remove; lookup names with add and remove and the note **Seeded names are checked against their known answers; a name you add counts as resolved whenever the resolver answers it.** Adding an upstream resolver creates a Resolver-scope target with profile `Resolver` and probe `dns`; its default name is `{address} directly`. Removing a lookup name never removes a seeded name's expected addresses from the others.
- Wizard (spec §5.3): a step **Which public resolver do you use?** after the anchors step, offering the anchor catalog entries plus **I don't know**, which picks the first chosen anchor; one sentence of why: **This lets ConnectionWatcher tell your resolver from everyone's.** `SetupApplyRequest` gains `string? UpstreamResolverAddress`; the catalog entries carry their lookup name, and a test pins that every catalog lookup name is one of `DnsLookupName.Seeded`.
- Health: code `dns-over-https`, message `Windows resolves through DNS-over-HTTPS for {address}; the check measures port 53, so it may not reflect what Windows actually uses.`; raised for each enabled system resolver address that a DoH reader reports as configured; the production reader looks under `HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\*\DohInterfaceSettings\Doh\{address}` for a non-zero `DohFlags` and reports nothing on any error or on non-Windows.
- Repo rules: tests wait for conditions in deadline loops, never sleep-then-assert; no personal endpoints; UTF-8 via the Edit tool; commit per task; never push.
- Test commands: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~<Class>"`; client `npm --prefix Programs/ConWatch/ClientApp test -- --run`.

## Review Focus

1. **A status snapshot without a `dns` block** (an older server, or `dns: null` in archive mode) must render the dashboard with no section and no crash. Task 1 (store) and Task 2 (section) test it.
2. **Removing the lookup name a seeded entry uses** must leave the other seeded names' expected addresses intact in the JSON the page writes. Task 3 tests the page's JSON builder.
3. **A system resolver disabled by the admin** must appear on the DNS page (from config) with its switch off, even though it has no monitor. Task 3 tests it.
4. **"I don't know" with no anchors chosen** must not fail the wizard; no upstream resolver is created. Task 4 tests it.
5. **The DoH registry reader on a machine without the keys** must report nothing and never throw. Task 5 tests the reader's parsing with a fake and the production reader's no-throw on this machine.

## File Structure

**Create**
- `Programs/ConWatch/ClientApp/src/components/NameResolutionSection.tsx` (+ `.test.tsx`) — the dashboard section.
- `Programs/ConWatch/ClientApp/src/lib/dnsSummary.ts` (+ `.test.ts`) — pure helpers: heading summary, which lookup names to show.
- `Programs/ConWatch/Pages/Config/Dns.cshtml` + `Dns.cshtml.cs` — the config page.
- `Programs/ConWatch/wwwroot/lib/site-dns.js` — the page's script (lookup-name list editing, resolver switches).
- `Core/ConCore/Dns/IDohDetector.cs` — the `IDohDetector` interface and `NullDohDetector` (ConPersistence references ConCore, not ConInfrastructure, so the interface lives in core).
- `Libraries/ConInfrastructure/Dns/DohConfiguration.cs` — the Windows registry reader and `DohConfiguration.ForThisPlatform()`.
- Tests: `Tests/ConWatch.Tests/DnsPageTests.cs`, `Tests/ConWatch.Tests/DnsWizardTests.cs`, `Tests/ConEngine.Tests/DohDetectorTests.cs`, `Tests/ConPersistence.Tests/ConfigHealthTests.cs` (extended).

**Modify**
- `src/lib/types.ts`, `src/lib/statusStore.ts`, `src/dashboard.tsx`, `src/components/VerdictBanner.tsx` (+ tests).
- `Programs/ConWatch/Pages/Index.cshtml`, `Index.cshtml.cs` — fallback section.
- `Programs/ConWatch/Pages/Config/_ConfigTabs.cshtml` — the DNS tab.
- `Programs/ConWatch/Pages/Setup.cshtml`, `wwwroot/lib/site-setup.js`, `Programs/ConWatch/SetupService.cs`, `Api/SetupEndpoints.cs` — the wizard step.
- `Libraries/ConPersistence/Config/ConfigHealth.cs`, `Programs/ConWatch/Startup/ConWatchServices.cs` — DoH.
- `README.md`, the spec — docs.

---

### Task 1: The `dns` block on the client, and the LinkDown banner line

**Files:**
- Modify: `Programs/ConWatch/ClientApp/src/lib/types.ts`, `src/lib/statusStore.ts`, `src/components/VerdictBanner.tsx`, `src/dashboard.tsx`
- Test: `src/lib/statusStore.test.ts`, `src/components/VerdictBanner.test.ts`

**Interfaces:**
- Produces (types.ts):
  ```ts
  export interface DnsResolverStatus { id: string; address: string; label: string; state: string; detail: string | null; enabled: boolean; }
  export interface DnsLookupNameStatus { name: string; lastResult: "ok" | "miss" | "filtered" | "none" | string; detail: string | null; }
  export interface DnsBlame { kind: "YourResolver" | "NoResolver" | string; message: string; resolvers: string[]; }
  export interface DnsStatus { watched: boolean; state: "Unknown" | "Ok" | "Degraded" | "Filtered" | "Down" | string;
    systemResolvers: DnsResolverStatus[]; upstreamResolvers: DnsResolverStatus[]; blame: DnsBlame | null; lookupNames: DnsLookupNameStatus[]; }
  ```
  `Snapshot.dns?: DnsStatus | null`; `StoreState.dns: DnsStatus | null` (null when the snapshot lacks it); `VerdictBanner` takes `dns?: DnsStatus | null` and `slabFor(verdict, dns?)` returns `caveat2: string | null` (the second line), rendered under the caveat in the same small style.

- [ ] **Step 1: Write the failing tests**

Append to `statusStore.test.ts` (follow the file's existing snapshot fixture):

```ts
  it("keeps the dns block from a snapshot and tolerates its absence", () => {
    const store = createStatusStore();
    store.applySnapshot({ verdict: null, targets: [], pools: [] });
    expect(store.getState().dns).toBeNull();
    store.applySnapshot({ verdict: null, targets: [], pools: [], dns: {
      watched: true, state: "Ok", systemResolvers: [], upstreamResolvers: [], blame: null, lookupNames: [],
    } });
    expect(store.getState().dns?.state).toBe("Ok");
    store.applySnapshot({ verdict: null, targets: [], pools: [], dns: null });
    expect(store.getState().dns).toBeNull();
  });
```

Append to `VerdictBanner.test.ts`:

```ts
  it("LinkDown says name lookups are failing too only when dns is Down", () => {
    const dnsDown = { watched: true, state: "Down", systemResolvers: [], upstreamResolvers: [], blame: null, lookupNames: [] };
    const dnsOk = { ...dnsDown, state: "Ok" };
    expect(slabFor(v("LinkDown"), dnsDown).caveat2).toBe("Name lookups are failing too");
    expect(slabFor(v("LinkDown"), dnsOk).caveat2).toBeNull();
    expect(slabFor(v("LinkDown")).caveat2).toBeNull();
    expect(slabFor(v("Healthy"), dnsDown).caveat2).toBeNull(); // only the LinkDown slab carries it
  });
```

- [ ] **Step 2: Run to verify they fail**

Run: `npm --prefix Programs/ConWatch/ClientApp test -- --run`
Expected: FAIL (type errors / `caveat2` undefined).

- [ ] **Step 3: Implement**

`types.ts`: add the four interfaces above and `dns?: DnsStatus | null;` to `Snapshot`. `statusStore.ts`: add `dns: DnsStatus | null` to `StoreState` (initial `null`), and in `applySnapshot` set `state.dns = snap.dns ?? null;`. `VerdictBanner.tsx`: add `caveat2: string | null` to the `Slab` interface; every existing case sets `caveat2: null`; change the signature to `export function slabFor(verdict: VerdictStatus, dns?: DnsStatus | null): Slab` and in the `LinkDown` case set `caveat2: dns?.state === "Down" ? "Name lookups are failing too" : null`; the component takes `dns?: DnsStatus | null`, calls `slabFor(verdict, dns)`, and renders `slab.caveat2` under the caveat with the same small style (no mark). `dashboard.tsx`: `<VerdictBanner verdict={state.verdict} dns={state.dns} connected={state.connected} clock={clock} />`.

- [ ] **Step 4: Run the client tests**

Run: `npm --prefix Programs/ConWatch/ClientApp test -- --run`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/ClientApp/src/lib/types.ts Programs/ConWatch/ClientApp/src/lib/statusStore.ts Programs/ConWatch/ClientApp/src/lib/statusStore.test.ts Programs/ConWatch/ClientApp/src/components/VerdictBanner.tsx Programs/ConWatch/ClientApp/src/components/VerdictBanner.test.ts Programs/ConWatch/ClientApp/src/dashboard.tsx
git commit -m "feat(ui): the dns block reaches the client; LinkDown says name lookups are failing too"
```

---

### Task 2: The "Name resolution" dashboard section, React and fallback

**Files:**
- Create: `src/lib/dnsSummary.ts`, `src/lib/dnsSummary.test.ts`, `src/components/NameResolutionSection.tsx`, `src/components/NameResolutionSection.test.tsx`
- Modify: `src/dashboard.tsx`, `Programs/ConWatch/Pages/Index.cshtml`, `Programs/ConWatch/Pages/Index.cshtml.cs`
- Test: `Tests/ConWatch.Tests/DnsPageTests.cs` (the fallback case; the file is created here and extended in Task 3)

**Interfaces:**
- Produces: `dnsSummary.ts` — `export function dnsHeadline(dns: DnsStatus | null, verdictState: string | null): string | null` (null when `dns` is null; the Global Constraints wording; `"isn't being watched"` when `!watched`), `export function troubledNames(dns: DnsStatus): DnsLookupNameStatus[]` (those with `lastResult` `miss` or `filtered`). `NameResolutionSection({ dns, verdictState })` renders `<div id="dns-section">` with `<h4>Name resolution <small>({headline})</small></h4>`, a `<ul class="cw-tree">` of resolver rows (`TargetRow`-like markup: `dot` + label + state chip + detail when not Responding), then a `<ul class="cw-names">` of troubled names, or the single not-watched line; renders nothing when `dns` is null.

- [ ] **Step 1: Write the failing tests**

`src/lib/dnsSummary.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { dnsHeadline, troubledNames } from "./dnsSummary";
import type { DnsStatus } from "./types";

const dns = (over: Partial<DnsStatus> = {}): DnsStatus => ({
  watched: true, state: "Ok", systemResolvers: [], upstreamResolvers: [], blame: null, lookupNames: [], ...over,
});

describe("dnsHeadline", () => {
  it("words each state for a person", () => {
    expect(dnsHeadline(null, "Healthy")).toBeNull();
    expect(dnsHeadline(dns({ watched: false, state: "Unknown" }), "Healthy")).toBe("isn't being watched");
    expect(dnsHeadline(dns({ state: "Unknown" }), "Healthy")).toBe("checking");
    expect(dnsHeadline(dns(), "Healthy")).toBe("working");
    expect(dnsHeadline(dns({ state: "Filtered" }), "Healthy")).toBe("working, but your resolver blocks the check names");
    expect(dnsHeadline(dns({ state: "Degraded" }), "Healthy")).toBe("one resolver isn't answering");
    expect(dnsHeadline(dns({ state: "Down" }), "DnsDown")).toBe("not working");
    expect(dnsHeadline(dns({ state: "Down" }), "Healthy")).toBe("re-checking");
    expect(dnsHeadline(dns({ state: "Down" }), "LinkDown")).toBe("re-checking");
  });
});

describe("troubledNames", () => {
  it("lists only names with a miss or a filtered result", () => {
    const d = dns({ lookupNames: [
      { name: "dns.google", lastResult: "ok", detail: null },
      { name: "one.one.one.one", lastResult: "filtered", detail: "Filtered: 0.0.0.0 (one.one.one.one)" },
      { name: "example.org", lastResult: "miss", detail: "ServFail (example.org)" },
      { name: "never.test", lastResult: "none", detail: null },
    ] });
    expect(troubledNames(d).map((n) => n.name)).toEqual(["one.one.one.one", "example.org"]);
  });
});
```

`src/components/NameResolutionSection.test.tsx`:

```tsx
import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { NameResolutionSection } from "./NameResolutionSection";
import type { DnsStatus } from "../lib/types";

const dns = (over: Partial<DnsStatus> = {}): DnsStatus => ({
  watched: true, state: "Ok", blame: null, lookupNames: [], upstreamResolvers: [],
  systemResolvers: [{ id: "a", address: "10.0.0.1", label: "Your resolver 10.0.0.1", state: "Responding", detail: null, enabled: true }],
  ...over,
});

describe("NameResolutionSection", () => {
  it("renders nothing without a dns block", () => {
    expect(renderToStaticMarkup(<NameResolutionSection dns={null} verdictState="Healthy" />)).toBe("");
  });
  it("shows the not-watched line and nothing else when the watch is off", () => {
    const html = renderToStaticMarkup(<NameResolutionSection dns={dns({ watched: false, state: "Unknown", systemResolvers: [] })} verdictState="Healthy" />);
    expect(html).toContain("Name resolution isn't being watched.");
    expect(html).not.toContain("cw-tree");
  });
  it("lists resolvers with a chip, the detail only when not Responding, and troubled names only", () => {
    const html = renderToStaticMarkup(<NameResolutionSection dns={dns({
      state: "Degraded",
      systemResolvers: [
        { id: "a", address: "10.0.0.1", label: "Your resolver 10.0.0.1", state: "Responding", detail: null, enabled: true },
        { id: "b", address: "10.0.0.2", label: "Your resolver 10.0.0.2", state: "Down", detail: "TimedOut (dns.google)", enabled: true },
      ],
      upstreamResolvers: [{ id: "u", address: "1.1.1.1", label: "1.1.1.1 directly", state: "Responding", detail: null, enabled: true }],
      lookupNames: [
        { name: "dns.google", lastResult: "ok", detail: null },
        { name: "example.org", lastResult: "miss", detail: "ServFail (example.org)" },
      ],
    })} verdictState="Healthy" />);
    expect(html).toContain("one resolver isn&#x27;t answering");
    expect(html).toContain("Your resolver 10.0.0.1");
    expect(html).toContain("1.1.1.1 directly");
    expect(html).toContain("TimedOut (dns.google)");
    expect(html).toContain("example.org");
    expect(html).not.toContain(">dns.google<"); // a healthy name is not listed
    expect(html).toContain("Down");
  });
});
```

(`renderToStaticMarkup` escapes apostrophes as `&#x27;`; match the escaped form as the LAN test does for its text.)

- [ ] **Step 2: Run to verify they fail**

Run: `npm --prefix Programs/ConWatch/ClientApp test -- --run`
Expected: FAIL (modules missing).

- [ ] **Step 3: Implement**

`src/lib/dnsSummary.ts`:

```ts
// Pure helpers for the "Name resolution" section (unit-tested).
import type { DnsLookupNameStatus, DnsStatus } from "./types";

/** The heading's parenthetical, in a person's words (spec 2026-10-10 §5.2; ruling 2026-10-15 for Down-while-healthy). */
export function dnsHeadline(dns: DnsStatus | null, verdictState: string | null): string | null {
  if (!dns) return null;
  if (!dns.watched) return "isn't being watched";
  switch (dns.state) {
    case "Ok": return "working";
    case "Filtered": return "working, but your resolver blocks the check names";
    case "Degraded": return "one resolver isn't answering";
    case "Down": return verdictState === "DnsDown" ? "not working" : "re-checking";
    default: return "checking";
  }
}

/** Lookup names worth a line: a miss or a block. Healthy and never-asked names stay quiet. */
export function troubledNames(dns: DnsStatus): DnsLookupNameStatus[] {
  return dns.lookupNames.filter((n) => n.lastResult === "miss" || n.lastResult === "filtered");
}
```

`src/components/NameResolutionSection.tsx`:

```tsx
// "Name resolution": the host's resolvers and the check names, beside the LAN
// section (spec 2026-10-10 §5.2, D5). It never colours the verdict or the tray.
import { dnsHeadline, troubledNames } from "../lib/dnsSummary";
import type { DnsResolverStatus, DnsStatus } from "../lib/types";

const CHIP_COLOR: Record<string, string> = {
  Faltering: "var(--cw-warn)", Recovering: "var(--cw-warn)",
  Down: "var(--cw-down)", Unreachable: "var(--cw-unreach)",
  Starting: "var(--cw-quiet)", Initializing: "var(--cw-quiet)",
};

function ResolverRow({ r }: { r: DnsResolverStatus }) {
  const chip = CHIP_COLOR[r.state];
  return (
    <li>
      <div className="cw-row">
        <span className={`dot ${r.state.toLowerCase()}`}></span>
        <span>{r.label}</span>
        <span className="addr">{r.address}</span>
        <span className="stats">
          <span className="statechip" style={chip ? {
            color: chip, background: `color-mix(in srgb, ${chip} 14%, transparent)`,
          } : undefined}>{r.state}</span>
          {r.state !== "Responding" && r.detail && <span className="sep"> · </span>}
          {r.state !== "Responding" && r.detail && <small className="detail">{r.detail}</small>}
        </span>
      </div>
    </li>
  );
}

export function NameResolutionSection({ dns, verdictState }: {
  dns: DnsStatus | null; verdictState: string | null;
}) {
  if (!dns) return null;
  const headline = dnsHeadline(dns, verdictState);
  if (!dns.watched) {
    return (
      <div id="dns-section" style={{ marginTop: "1.5rem" }}>
        <h4>Name resolution <small>({headline})</small></h4>
        <p>Name resolution isn't being watched.</p>
      </div>
    );
  }
  const names = troubledNames(dns);
  return (
    <div id="dns-section" style={{ marginTop: "1.5rem" }}>
      <h4>Name resolution <small>({headline})</small></h4>
      <ul className="cw-tree">
        {dns.systemResolvers.map((r) => <ResolverRow key={r.id} r={r} />)}
        {dns.upstreamResolvers.map((r) => <ResolverRow key={r.id} r={r} />)}
      </ul>
      {names.length > 0 && (
        <ul className="cw-names">
          {names.map((n) => (
            <li key={n.name}>
              <span>{n.name}</span>
              <small className="detail"> — {n.lastResult === "filtered" ? "blocked by your resolver" : "not resolving"}
                {n.detail ? ` (${n.detail})` : ""}</small>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
```

`dashboard.tsx`: import it and render `<NameResolutionSection dns={state.dns} verdictState={state.verdict?.state ?? null} />` directly after `<LanSection targets={state.targets} />`.

Fallback: `Index.cshtml.cs` gains `public List<TargetMonitor> ResolverMonitors { get; } = new();` and in the monitors loop the existing `if (monitor.Target.Scope == TargetScope.Resolver) continue;` becomes `{ ResolverMonitors.Add(monitor); continue; }`. `Index.cshtml`, after the LAN block:

```cshtml
    @if (Model.ResolverMonitors.Count > 0)
    {
        <h4>Name resolution</h4>
        <ul class="cw-tree">
            @foreach (var monitor in Model.ResolverMonitors)
            {
                <li>
                    <div class="cw-row">
                        <span class="dot @monitor.CurrentState.ToString().ToLowerInvariant()"></span>
                        <span>@monitor.Target.Name</span>
                        <span class="stats">@monitor.CurrentState</span>
                    </div>
                </li>
            }
        </ul>
    }
```

Create `Tests/ConWatch.Tests/DnsPageTests.cs` with one test for the fallback (the file grows in Task 3):

```csharp
using System.Net.Http.Json;
using ConWatch.NameResolution;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class DnsPageTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public DnsPageTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task SeedWithResolverAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");
        await _factory.Services.GetRequiredService<SystemResolverService>().ReconcileNowAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_fallback_dashboard_lists_resolvers_under_the_Name_resolution_heading_not_in_the_tree()
    {
        await SeedWithResolverAsync();
        string html = "";
        await ConfigApiTests.Eventually(async () =>
        {
            html = await _client.GetStringAsync("/");
            return html.Contains("Name resolution") && html.Contains("Your resolver 10.0.0.53");
        });
        var fallback = html[html.IndexOf("id=\"dashboard-root\"", StringComparison.Ordinal)..];
        var heading = fallback.IndexOf("Name resolution", StringComparison.Ordinal);
        var tree = fallback.IndexOf("<ul class=\"cw-tree\">", StringComparison.Ordinal);
        Assert.True(tree >= 0 && tree < heading, "the verdict tree comes first");
        Assert.True(fallback.IndexOf("Your resolver 10.0.0.53", StringComparison.Ordinal) > heading,
            "the resolver is listed under the heading, never as a tree root");
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `npm --prefix Programs/ConWatch/ClientApp test -- --run` and `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~DnsPageTests|FullyQualifiedName~LocalScopeUiTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/ClientApp/src Programs/ConWatch/Pages/Index.cshtml Programs/ConWatch/Pages/Index.cshtml.cs Tests/ConWatch.Tests/DnsPageTests.cs
git commit -m "feat(ui): Name resolution section on the dashboard, with a server-rendered fallback"
```

---

### Task 3: The DNS config page

**Files:**
- Create: `Programs/ConWatch/Pages/Config/Dns.cshtml`, `Programs/ConWatch/Pages/Config/Dns.cshtml.cs`, `Programs/ConWatch/wwwroot/lib/site-dns.js`
- Modify: `Programs/ConWatch/Pages/Config/_ConfigTabs.cshtml`
- Test: `Tests/ConWatch.Tests/DnsPageTests.cs` (extend)

**Interfaces:**
- Produces: `DnsModel` with `bool Enabled`, `IReadOnlyList<DnsLookupName> LookupNames`, `IReadOnlyList<TargetDto> SystemResolvers` (managed, including disabled, ordered by name), `IReadOnlyList<TargetDto> UpstreamResolvers` (Resolver scope, not managed), `IReadOnlyList<HealthWarning> Warnings`, `string LookupNamesJson` (the stored JSON, for the script). The page writes through `PUT /api/v1/settings` (`Dns.Enabled`, `Dns.LookupNames`), `PUT /api/v1/targets/{id}` (a managed row with `enabled` flipped), `POST /api/v1/targets` (an upstream resolver), `DELETE /api/v1/targets/{id}`.
- `site-dns.js` exposes `window.dnsLookupNamesJson(names)` for testing? No — keep the JSON builder pure and tested through the page: the test reads back `Dns.LookupNames` after driving the API the same way the script does. The script's builder: `buildLookupNamesJson(existing, names)` keeps each existing entry (with its `expected`) whose name is still in `names`, appends `{ name }` for new names, in the order of `names`.

- [ ] **Step 1: Write the failing tests**

Append to `DnsPageTests`:

```csharp
    [Fact]
    public async Task The_DNS_page_lists_system_resolvers_including_disabled_ones_and_the_seeded_names()
    {
        await SeedWithResolverAsync();
        var targets = await _client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets");
        var managed = targets!.Single(t => t.SystemManaged);
        (await _client.PutAsJsonAsync($"/api/v1/targets/{managed.Id}", managed with { Enabled = false })).EnsureSuccessStatusCode();

        var html = await _client.GetStringAsync("/Config/Dns");
        Assert.Contains("found from your network settings", html);
        Assert.Contains("Your resolver 10.0.0.53", html);
        Assert.Contains("data-resolver-enabled=\"false\"", html);      // the disabled one is still listed
        Assert.Contains("dns.google", html);
        Assert.Contains("Seeded names are checked against their known answers", html);
        Assert.Contains("href=\"/Config/Dns\"", await _client.GetStringAsync("/Config/Targets")); // the tab
    }

    [Fact]
    public async Task Adding_and_removing_an_upstream_resolver_and_a_lookup_name_round_trips_through_the_endpoints_the_page_uses()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        // Upstream resolver, as the page posts it.
        var created = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "1.1.1.1 directly", address = "1.1.1.1", profile = "Resolver", probe = "dns", scope = "Resolver",
        });
        created.EnsureSuccessStatusCode();
        var html = await _client.GetStringAsync("/Config/Dns");
        Assert.Contains("1.1.1.1 directly", html);

        // Lookup names: the page keeps seeded entries' expected addresses and appends plain names.
        var seeded = ConCore.Dns.DnsLookupName.Seeded;
        var kept = seeded.Where(n => n.Name != "dns.opendns.com").ToList();
        var json = ConCore.Dns.DnsLookupName.ToJson(kept.Append(new ConCore.Dns.DnsLookupName("router.lan", [])));
        (await _client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?> { ["Dns.LookupNames"] = json }))
            .EnsureSuccessStatusCode();
        html = await _client.GetStringAsync("/Config/Dns");
        Assert.DoesNotContain("dns.opendns.com", html);
        Assert.Contains("router.lan", html);
        var stored = ConCore.Dns.DnsLookupName.FromJson(
            (await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings"))!["Dns.LookupNames"]);
        Assert.Equal(["8.8.8.8", "8.8.4.4"], stored.Single(n => n.Name == "dns.google").Expected); // untouched
        Assert.Empty(stored.Single(n => n.Name == "router.lan").Expected);
    }
```

(If `GET /api/v1/settings` returns a different shape, read `Api/SettingsEndpoints.cs` and adapt the deserialization; the assertion is on the stored JSON.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~DnsPageTests"`
Expected: FAIL (404 on `/Config/Dns`).

- [ ] **Step 3: Implement the page model**

`Programs/ConWatch/Pages/Config/Dns.cshtml.cs`:

```csharp
using ConCore.Dns;
using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>The name-resolution watch (spec 2026-10-10 §5.3). Admin-locked by
/// convention (AdminLockPageFilter on /Config). Server-rendered from the stored
/// config so a resolver the admin disabled is still listed.</summary>
public class DnsModel(ConfigStore store, ConfigHealth health, SettingsStore settings) : PageModel
{
    public bool Enabled { get; private set; } = true;
    public string LookupNamesJson { get; private set; } = "[]";
    public IReadOnlyList<DnsLookupName> LookupNames { get; private set; } = [];
    public IReadOnlyList<TargetDto> SystemResolvers { get; private set; } = [];
    public IReadOnlyList<TargetDto> UpstreamResolvers { get; private set; } = [];
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Enabled = await settings.GetAsync(SettingKeys.DnsEnabled) != "false";
        LookupNamesJson = await settings.GetAsync(SettingKeys.DnsLookupNames) ?? "[]";
        LookupNames = DnsLookupName.FromJson(LookupNamesJson);
        var config = await store.ExportAsync();
        var resolvers = config.Targets.Where(t => t.Scope == TargetScope.Resolver).OrderBy(t => t.Name).ToList();
        SystemResolvers = resolvers.Where(t => t.SystemManaged).ToList();
        UpstreamResolvers = resolvers.Where(t => !t.SystemManaged).ToList();
        Warnings = await health.EvaluateAsync();
    }
}
```

- [ ] **Step 4: Implement the page**

`_ConfigTabs.cshtml`: add `<a class="@(Model == "dns" ? "active" : "")" href="/Config/Dns">DNS</a>` after Pools.

`Programs/ConWatch/Pages/Config/Dns.cshtml`:

```cshtml
@page
@using System.Text.Json
@using ConPersistence.Settings
@model ConWatch.Pages.Config.DnsModel
@{
    ViewData["Title"] = "DNS — ConnectionWatcher";
    var json = (object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

<div class="cw-pageband">
    <div class="titles">
        <h2>DNS</h2>
        <p>The name-resolution watch: the resolvers this machine uses, the public resolver you named, and the names they are asked for.</p>
    </div>
</div>
<partial name="_ConfigTabs" model="@("dns")" />
<partial name="_HealthBanner" model="Model.Warnings" />

<h3>Watch</h3>
<ul class="form-errors" id="watch-errors"></ul>
<form id="watch-form">
    <label>
        <input type="checkbox" role="switch" id="dns-enabled" @(Model.Enabled ? "checked" : "") />
        Watch name resolution
    </label>
    <input type="hidden" name="@SettingKeys.DnsEnabled" value="@(Model.Enabled ? "true" : "false")" />
    <p><small>Off: the resolvers below are kept but not probed, and the verdict never says name lookups are failing.</small></p>
    <button type="submit">Save</button>
</form>

<h3>Your resolvers <small>found from your network settings</small></h3>
<table>
    <thead><tr><th>Name</th><th>Address</th><th>Enabled</th></tr></thead>
    <tbody>
    @foreach (var r in Model.SystemResolvers)
    {
        <tr data-resolver-id="@r.Id" data-resolver-enabled="@(r.Enabled ? "true" : "false")" data-json="@json(r)">
            <td>@r.Name</td><td>@r.Address</td>
            <td><input type="checkbox" role="switch" data-toggle-resolver @(r.Enabled ? "checked" : "") aria-label="Enabled" /></td>
        </tr>
    }
    @if (Model.SystemResolvers.Count == 0)
    {
        <tr><td colspan="3"><i>None found yet — they appear within a minute of the first start.</i></td></tr>
    }
    </tbody>
</table>

<h3>Public resolver <small>asked directly, to tell your resolver from everyone's</small></h3>
<table>
    <thead><tr><th>Name</th><th>Address</th><th></th></tr></thead>
    <tbody>
    @foreach (var r in Model.UpstreamResolvers)
    {
        <tr>
            <td>@r.Name</td><td>@r.Address</td>
            <td><button class="outline" data-delete-resolver="@r.Id" data-name="@r.Name">Remove</button></td>
        </tr>
    }
    </tbody>
</table>
<form id="upstream-form" class="grid">
    <label>Address <input name="address" placeholder="1.1.1.1" required /></label>
    <label>Name <input name="name" placeholder="1.1.1.1 directly" /></label>
    <div style="align-self: end"><button type="submit">Add resolver</button></div>
</form>
<ul class="form-errors" id="upstream-errors"></ul>

<h3>Lookup names</h3>
<p><small>Seeded names are checked against their known answers; a name you add counts as resolved whenever the resolver answers it.</small></p>
<ul id="name-list">
    @foreach (var n in Model.LookupNames)
    {
        <li data-name="@n.Name">@n.Name @if (n.HasExpected) { <span class="cw-chip">seeded</span> }
            <button class="outline" type="button" data-remove-name="@n.Name">Remove</button></li>
    }
</ul>
<form id="name-form" class="grid">
    <label>Add a name <input name="name" placeholder="router.lan" required /></label>
    <div style="align-self: end"><button type="submit">Add name</button></div>
</form>
<ul class="form-errors" id="name-errors"></ul>

@section Scripts {
    <script src="~/lib/site-config.js" asp-append-version="true"></script>
    <script>window.dnsLookupNames = @Html.Raw(Model.LookupNamesJson);</script>
    <script src="~/lib/site-dns.js" asp-append-version="true"></script>
}
```

`Programs/ConWatch/wwwroot/lib/site-dns.js`:

```js
// DNS config page (spec 2026-10-10 §5.3): writes through the existing
// admin-locked settings and targets endpoints. `window.dnsLookupNames` is the
// stored list (seeded entries carry their expected addresses).

/** Keeps every existing entry still named (so a seeded name keeps its expected
 *  addresses), appends plain entries for new names, in the given order. */
function buildLookupNamesJson(existing, names) {
    const byName = new Map(existing.map((e) => [e.name.toLowerCase(), e]));
    return JSON.stringify(names.map((n) => byName.get(n.toLowerCase()) ?? { name: n }));
}

async function saveLookupNames(names) {
    const errors = document.getElementById("name-errors");
    clearErrors(errors);
    const result = await apiFetch("PUT", "/api/v1/settings", {
        "Dns.LookupNames": buildLookupNamesJson(window.dnsLookupNames ?? [], names),
    });
    if (result.ok) location.reload(); else showErrors(errors, result);
}

function currentNames() {
    return [...document.querySelectorAll("#name-list li")].map((li) => li.dataset.name);
}

document.getElementById("dns-enabled").addEventListener("change", (e) => {
    document.querySelector("#watch-form [name='Dns.Enabled']").value = e.target.checked ? "true" : "false";
});
wireSettingsForm("watch-form", "watch-errors");

document.querySelectorAll("[data-toggle-resolver]").forEach((box) => box.addEventListener("change", async (e) => {
    const row = e.target.closest("tr");
    const dto = JSON.parse(row.dataset.json);
    dto.enabled = e.target.checked;
    const result = await apiFetch("PUT", `/api/v1/targets/${row.dataset.resolverId}`, dto);
    if (result.ok) location.reload(); else { e.target.checked = !e.target.checked; showErrors(document.getElementById("watch-errors"), result); }
}));

document.getElementById("upstream-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errors = document.getElementById("upstream-errors");
    clearErrors(errors);
    const address = e.target.elements.address.value.trim();
    const name = e.target.elements.name.value.trim() || `${address} directly`;
    const result = await apiFetch("POST", "/api/v1/targets", {
        name, address, profile: "Resolver", probe: "dns", scope: "Resolver",
    });
    if (result.ok) location.reload(); else showErrors(errors, result);
});

document.querySelectorAll("[data-delete-resolver]").forEach((btn) => btn.addEventListener("click", async () => {
    if (!(await confirmDialog(`Remove ${btn.dataset.name}?`, { confirmLabel: "Remove" }))) return;
    const result = await apiFetch("DELETE", `/api/v1/targets/${btn.dataset.deleteResolver}`);
    if (result.ok) location.reload(); else showErrors(document.getElementById("upstream-errors"), result);
}));

document.getElementById("name-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const name = e.target.elements.name.value.trim();
    if (!name) return;
    saveLookupNames([...currentNames(), name]);
});
document.querySelectorAll("[data-remove-name]").forEach((btn) => btn.addEventListener("click", () =>
    saveLookupNames(currentNames().filter((n) => n !== btn.dataset.removeName))));
```

(`apiFetch`, `clearErrors`, `showErrors`, `wireSettingsForm`, `confirmDialog` come from `site-config.js`; read it for `confirmDialog`'s exact return contract and match it.) If `site-config.js`'s `apiFetch` needs a body for DELETE, pass `undefined` as the existing pages do.

- [ ] **Step 5: Run the tests**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~DnsPageTests|FullyQualifiedName~AdminLockTests|FullyQualifiedName~ApiSurfaceLockTests"`
Expected: PASS (the page is under `/Config`, so the admin lock covers it; no new API routes).

- [ ] **Step 6: Commit**

```bash
git add Programs/ConWatch/Pages/Config/Dns.cshtml Programs/ConWatch/Pages/Config/Dns.cshtml.cs Programs/ConWatch/Pages/Config/_ConfigTabs.cshtml Programs/ConWatch/wwwroot/lib/site-dns.js Tests/ConWatch.Tests/DnsPageTests.cs
git commit -m "feat(config): DNS page — watch switch, your resolvers (incl. disabled), the public resolver, lookup names"
```

---

### Task 4: The wizard's resolver step

**Files:**
- Modify: `Programs/ConWatch/SetupService.cs` (`SetupTarget`, `SetupApplyRequest`, `AnchorCatalog`, `ApplyAsync`), `Programs/ConWatch/Pages/Setup.cshtml`, `Programs/ConWatch/wwwroot/lib/site-setup.js`
- Test: `Tests/ConWatch.Tests/DnsWizardTests.cs`

**Interfaces:**
- Produces: `SetupTarget(string Name, string Address, string? Operator = null, string? LookupName = null)`; `AnchorCatalog` entries carry `LookupName` (`dns.google`, `one.one.one.one`, `dns.quad9.net`, `dns.opendns.com`); `SetupApplyRequest(..., string? IspName = null, string? UpstreamResolverAddress = null)`. `ApplyAsync`: the upstream address is `UpstreamResolverAddress` when non-blank, else the first `AnchorAddresses` entry, else none; when there is one and no Resolver-scope target exists at that address, add `TargetDto { Name = Unique("{address} directly"), Address, Profile = "Resolver", Probe = "dns", Scope = Resolver }`. The wizard page's step 3 is the resolver question (WAN becomes step 4); `site-setup.js` renders radio buttons from the catalog plus "I don't know" (checked by default) and posts `upstreamResolverAddress` (null for "I don't know").

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConWatch.Tests/DnsWizardTests.cs` (reuse `SetupApiTests`' fake gateway/WAN factory pattern by copying its two fakes and `Factory` class):

```csharp
using System.Net.Http.Json;
using ConCore.Dns;
using ConCore.Targets;
using ConInfrastructure.Discovery;
using ConPersistence.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConWatch.Tests;

public class DnsWizardTests : IDisposable
{
    private sealed class FakeGateway : IGatewayDiscovery
    {
        public Task<IReadOnlyList<GatewayCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GatewayCandidate>>([new GatewayCandidate("192.168.1.1", "default-route", true, 1)]);
    }
    private sealed class FakeWanProbe : IWanAddressProbe
    {
        public Task<WanProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new WanProbeResult("203.0.113.23", null, true, 12));
    }
    private sealed class Factory : ConWatchFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<IGatewayDiscovery>(); s.AddSingleton<IGatewayDiscovery, FakeGateway>();
                s.RemoveAll<IWanAddressProbe>(); s.AddSingleton<IWanAddressProbe, FakeWanProbe>();
            });
        }
    }

    private readonly Factory _factory = new();
    private readonly HttpClient _client;
    public DnsWizardTests() => _client = _factory.CreateClient();
    public void Dispose() => _factory.Dispose();

    private async Task<List<TargetDto>> TargetsAsync() => (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;

    [Fact]
    public void Every_catalog_entry_names_a_seeded_lookup_name()
    {
        foreach (var entry in SetupService.AnchorCatalog)
            Assert.Contains(DnsLookupName.Seeded, n => n.Name == entry.LookupName);
    }

    [Fact]
    public async Task A_named_resolver_becomes_an_upstream_Resolver_target()
    {
        var r = await _client.PostAsJsonAsync("/api/v1/setup/apply", new
        {
            router = new { name = "Router", address = "192.168.1.1" },
            anchorAddresses = new[] { "8.8.8.8", "1.1.1.1" },
            upstreamResolverAddress = "1.1.1.1",
        });
        r.EnsureSuccessStatusCode();
        var upstream = Assert.Single(await TargetsAsync(), t => t.Scope == TargetScope.Resolver);
        Assert.Equal("1.1.1.1", upstream.Address);
        Assert.Equal("1.1.1.1 directly", upstream.Name);
        Assert.Equal("Resolver", upstream.Profile);
        Assert.Equal("dns", upstream.Probe);
        Assert.False(upstream.SystemManaged);
        Assert.Null(upstream.Parent);
    }

    [Fact]
    public async Task I_dont_know_picks_the_first_chosen_anchor_and_no_anchors_means_no_resolver()
    {
        var r = await _client.PostAsJsonAsync("/api/v1/setup/apply", new
        {
            router = new { name = "Router", address = "192.168.1.1" },
            anchorAddresses = new[] { "9.9.9.9", "8.8.8.8" },
            upstreamResolverAddress = (string?)null,
        });
        r.EnsureSuccessStatusCode();
        Assert.Equal("9.9.9.9", Assert.Single(await TargetsAsync(), t => t.Scope == TargetScope.Resolver).Address);

        using var fresh = new Factory();
        var client = fresh.CreateClient();
        var r2 = await client.PostAsJsonAsync("/api/v1/setup/apply", new
        {
            router = new { name = "Router", address = "192.168.1.1" },
            anchorAddresses = Array.Empty<string>(),
        });
        r2.EnsureSuccessStatusCode();
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!, t => t.Scope == TargetScope.Resolver);
    }

    [Fact]
    public async Task Rerunning_the_wizard_does_not_duplicate_the_resolver()
    {
        var body = new
        {
            router = new { name = "Router", address = "192.168.1.1" },
            anchorAddresses = new[] { "8.8.8.8" },
            upstreamResolverAddress = "8.8.8.8",
        };
        (await _client.PostAsJsonAsync("/api/v1/setup/apply", body)).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/v1/setup/apply", body)).EnsureSuccessStatusCode();
        Assert.Single(await TargetsAsync(), t => t.Scope == TargetScope.Resolver);
    }

    [Fact]
    public async Task The_wizard_page_has_the_resolver_step()
    {
        var html = await _client.GetStringAsync("/Setup");
        Assert.Contains("Which public resolver do you use?", html);
        Assert.Contains("This lets ConnectionWatcher tell your resolver from everyone's.", html);
        Assert.Contains("id=\"resolver-list\"", html);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~DnsWizardTests"`
Expected: FAIL (compile: `LookupName`, `UpstreamResolverAddress`).

- [ ] **Step 3: Implement**

`SetupService.cs`: `public record SetupTarget(string Name, string Address, string? Operator = null, string? LookupName = null);` `public record SetupApplyRequest(SetupTarget Router, SetupTarget? OuterIp, List<string> AnchorAddresses, string? IspName = null, string? UpstreamResolverAddress = null);` catalog entries gain their lookup names (`"Google DNS", "8.8.8.8", "Google", "dns.google"` etc.). In `ApplyAsync`, inside the `EditDocumentAsync` lambda after the anchors loop:

```csharp
            // The public resolver asked directly (spec 2026-10-10 §5.3): the one named,
            // else the first anchor chosen. Reused when a Resolver target is already there.
            var upstream = string.IsNullOrWhiteSpace(request.UpstreamResolverAddress)
                ? request.AnchorAddresses.FirstOrDefault()
                : request.UpstreamResolverAddress.Trim();
            if (upstream is { Length: > 0 } && !doc.Targets.Any(t => t.Scope == TargetScope.Resolver
                    && t.Address.Equals(upstream, StringComparison.OrdinalIgnoreCase)))
            {
                Add(new TargetDto
                {
                    Name = Unique($"{upstream} directly", NameTaken), Address = upstream,
                    Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver,
                });
            }
```

`Setup.cshtml`: insert after `#step-anchors`:

```cshtml
<section id="step-resolver">
    <h4>3 · Which public resolver do you use?</h4>
    <p>This lets ConnectionWatcher tell your resolver from everyone's.
        <small>(If your router or Pi-hole forwards to one of these, pick it; otherwise "I don't know" uses your first anchor.)</small></p>
    <div id="resolver-list" aria-busy="true">loading…</div>
</section>
```

and renumber the WAN step to `4 ·`. `site-setup.js`: in `loadAnchors`, after the anchor checkboxes, fill `#resolver-list` with one radio per catalog entry (`name="resolver"`, `value=anchor.address`, label `${anchor.name} (${anchor.address})`) plus a final radio `value=""` labelled `I don't know`, checked; in the apply handler add `upstreamResolverAddress: document.querySelector("#resolver-list input:checked")?.value || null`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~DnsWizardTests|FullyQualifiedName~SetupApiTests|FullyQualifiedName~FirstRunWithResolverTests"`
Expected: PASS (existing wizard tests post no `upstreamResolverAddress`, so they now also get a resolver at their first anchor; adjust any `Assert.Equal(count, targets.Count)` in `SetupApiTests` by one and say so).

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/SetupService.cs Programs/ConWatch/Pages/Setup.cshtml Programs/ConWatch/wwwroot/lib/site-setup.js Tests/ConWatch.Tests/DnsWizardTests.cs Tests/ConWatch.Tests/SetupApiTests.cs
git commit -m "feat(setup): the wizard asks which public resolver you use and names it as the upstream resolver"
```

---

### Task 5: The `dns-over-https` health warning

**Files:**
- Create: `Core/ConCore/Dns/IDohDetector.cs`, `Libraries/ConInfrastructure/Dns/DohConfiguration.cs`
- Modify: `Libraries/ConPersistence/Config/ConfigHealth.cs`, `Programs/ConWatch/Startup/ConWatchServices.cs`
- Test: `Tests/ConEngine.Tests/DohDetectorTests.cs`, `Tests/ConPersistence.Tests/ConfigHealthTests.cs`

**Interfaces:**
- Produces (`ConCore.Dns`): `public interface IDohDetector { IReadOnlyList<string> AddressesUsingDoh(IEnumerable<string> resolverAddresses); }` and `NullDohDetector` (always empty). Produces (`ConInfrastructure.NameResolution`): `WindowsDohDetector(Func<IEnumerable<string>> dohAddresses)` with a parameterless production constructor that enumerates `HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\{iface}\DohInterfaceSettings\Doh\{address}` subkeys whose `DohFlags` value is a non-zero integer (any error, or not Windows → empty); `static IDohDetector DohConfiguration.ForThisPlatform()`. Layering: ConPersistence references ConCore and ConEngine only, never ConInfrastructure, so the interface sits in core and only the host wires the Windows reader in.
- `ConfigHealth` gains an optional trailing constructor parameter `IDohDetector? doh = null`; `AddDnsWarningsAsync` adds one `dns-over-https` warning per enabled system resolver address the detector reports, with the Global Constraints message.

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/DohDetectorTests.cs`:

```csharp
using ConCore.Dns;
using ConInfrastructure.NameResolution;

namespace ConEngine.Tests;

public class DohDetectorTests
{
    [Fact]
    public void Reports_only_the_resolvers_the_source_lists_case_insensitively()
    {
        var detector = new WindowsDohDetector(() => ["1.1.1.1", "FE80::1"]);
        Assert.Equal(["1.1.1.1", "fe80::1"], detector.AddressesUsingDoh(["10.0.0.1", "1.1.1.1", "fe80::1"]));
    }

    [Fact]
    public void A_throwing_source_reads_as_nothing()
    {
        var detector = new WindowsDohDetector(() => throw new UnauthorizedAccessException("registry"));
        Assert.Empty(detector.AddressesUsingDoh(["1.1.1.1"]));
    }

    [Fact]
    public void The_platform_detector_never_throws_on_this_machine() =>
        Assert.NotNull(DohConfiguration.ForThisPlatform().AddressesUsingDoh(["1.1.1.1", "8.8.8.8"]));

    [Fact]
    public void Registry_values_are_read_as_doh_only_when_DohFlags_is_non_zero()
    {
        // The parser over a fake registry walk: (interface, address, DohFlags)
        var rows = new (string Iface, string Address, object? Flags)[]
        {
            ("{A}", "1.1.1.1", 1), ("{A}", "8.8.8.8", 0), ("{B}", "9.9.9.9", 2L), ("{B}", "4.4.4.4", null), ("{B}", "5.5.5.5", "x"),
        };
        Assert.Equal(["1.1.1.1", "9.9.9.9"], WindowsDohDetector.AddressesFrom(rows));
    }
}
```

Append to `ConfigHealthTests.cs` (it has `_health`, `_settings`, `_service`, `Codes()`; the health evaluator must be rebuilt with a fake detector for this test — construct `new ConfigHealth(_factory, _settings, new WindowsDohDetector(() => ["192.168.1.1"]))`):

```csharp
    [Fact]
    public async Task A_system_resolver_that_Windows_resolves_over_DoH_gets_a_health_warning()
    {
        await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto { Name = "Your resolver 192.168.1.1", Address = "192.168.1.1", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true });
            return ConfigResult.Ok();
        });
        await _settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.DnsLookupNames] = DnsLookupName.ToJson(DnsLookupName.Seeded) });
        var health = new ConfigHealth(_factory, _settings, new FakeDoh(["192.168.1.1"]));
        var warnings = await health.EvaluateAsync();
        var doh = Assert.Single(warnings, w => w.Code == "dns-over-https");
        Assert.Equal("Windows resolves through DNS-over-HTTPS for 192.168.1.1; the check measures port 53, so it may not reflect what Windows actually uses.", doh.Message);
        Assert.DoesNotContain(await Codes(), c => c == "dns-over-https"); // the default evaluator has no detector
    }
```

Add this fake inside `ConfigHealthTests` (ConPersistence.Tests references only ConPersistence; the interface comes from ConCore, so no new project reference):

```csharp
    private sealed class FakeDoh(string[] configured) : ConCore.Dns.IDohDetector
    {
        public IReadOnlyList<string> AddressesUsingDoh(IEnumerable<string> resolverAddresses) =>
            resolverAddresses.Where(a => configured.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DohDetectorTests"` and `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~ConfigHealthTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`Core/ConCore/Dns/IDohDetector.cs`:

```csharp
namespace ConCore.Dns;

/// <summary>Which of the host's resolvers the OS reaches over DNS-over-HTTPS, so the
/// port-53 check can say it may not reflect what the OS really uses (spec 2026-10-10 §4.5).
/// The interface lives in core so ConfigHealth (persistence) can consume it; the
/// Windows reader lives in infrastructure and the host wires it in.</summary>
public interface IDohDetector
{
    IReadOnlyList<string> AddressesUsingDoh(IEnumerable<string> resolverAddresses);
}

public sealed class NullDohDetector : IDohDetector
{
    public IReadOnlyList<string> AddressesUsingDoh(IEnumerable<string> resolverAddresses) => [];
}
```

`Libraries/ConInfrastructure/Dns/DohConfiguration.cs`:

```csharp
using ConCore.Dns;
using Microsoft.Win32;

namespace ConInfrastructure.NameResolution;

/// <summary>
/// Windows keeps per-interface DoH settings under
/// HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\{iface}\DohInterfaceSettings\Doh\{address},
/// with a DohFlags value that is non-zero when DoH is on for that server. Best effort:
/// any error, or a layout we do not recognise, reads as "nothing configured".
/// </summary>
public sealed class WindowsDohDetector(Func<IEnumerable<string>> dohAddresses) : IDohDetector
{
    private const string Root = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters";

    public WindowsDohDetector() : this(ReadRegistry) { }

    public IReadOnlyList<string> AddressesUsingDoh(IEnumerable<string> resolverAddresses)
    {
        HashSet<string> configured;
        try
        {
            configured = dohAddresses().Select(a => a.ToLowerInvariant()).ToHashSet();
        }
        catch (Exception)
        {
            return [];
        }
        return resolverAddresses.Select(a => a.ToLowerInvariant()).Where(configured.Contains).Distinct().ToList();
    }

    /// <summary>The addresses whose DohFlags is a non-zero integer (int or long).</summary>
    public static IReadOnlyList<string> AddressesFrom(IEnumerable<(string Iface, string Address, object? Flags)> rows) =>
        rows.Where(r => r.Flags is int i && i != 0 || r.Flags is long l && l != 0)
            .Select(r => r.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<string> ReadRegistry()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var rows = new List<(string, string, object?)>();
        using var root = Registry.LocalMachine.OpenSubKey(Root);
        if (root is null)
            return [];
        foreach (var iface in root.GetSubKeyNames())
        {
            using var doh = root.OpenSubKey($@"{iface}\DohInterfaceSettings\Doh");
            if (doh is null)
                continue;
            foreach (var address in doh.GetSubKeyNames())
            {
                using var server = doh.OpenSubKey(address);
                rows.Add((iface, address, server?.GetValue("DohFlags")));
            }
        }
        return AddressesFrom(rows);
    }
}

public static class DohConfiguration
{
    public static IDohDetector ForThisPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsDohDetector() : new NullDohDetector();
}
```

(`Microsoft.Win32.Registry` is part of the .NET runtime on Windows; the project already targets `net10.0`. If the compiler flags platform compatibility (CA1416), mark `ReadRegistry` with `[System.Runtime.Versioning.SupportedOSPlatform("windows")]` and keep the `OperatingSystem.IsWindows()` guard.)

`ConfigHealth`: constructor `(IDbContextFactory<ConWatchDbContext> dbFactory, SettingsStore settings, IDohDetector? doh = null)`; in `AddDnsWarningsAsync`, after the existing warnings:

```csharp
        if (doh is not null && dnsEnabled)
        {
            var enabledSystem = await db.Targets.Where(t => t.Scope == TargetScope.Resolver && t.SystemManaged && t.Enabled)
                .Select(t => t.Address).ToListAsync();
            foreach (var address in doh.AddressesUsingDoh(enabledSystem))
                warnings.Add(new HealthWarning("dns-over-https",
                    $"Windows resolves through DNS-over-HTTPS for {address}; the check measures port 53, so it may not reflect what Windows actually uses."));
        }
```

`ConfigHealth` takes `ConCore.Dns.IDohDetector? doh = null` (add `using ConCore.Dns;`). `ConWatchServices.AddConWatchPersistence`: `services.AddSingleton<ConCore.Dns.IDohDetector>(_ => ConInfrastructure.NameResolution.DohConfiguration.ForThisPlatform());` before `ConfigHealth`'s registration at line 83 (DI picks the longer constructor). `ConWatchFactory`: `RemoveAll<ConCore.Dns.IDohDetector>()` and register `new ConCore.Dns.NullDohDetector()`, so host tests never read the test machine's registry.

- [ ] **Step 4: Run the tests**

Run: the two commands from Step 2, then `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~ConfigHealth|FullyQualifiedName~DnsPageTests|FullyQualifiedName~HostLifecycleTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/ConCore/Dns/IDohDetector.cs Libraries/ConInfrastructure/Dns/DohConfiguration.cs Libraries/ConPersistence/Config/ConfigHealth.cs Programs/ConWatch/Startup/ConWatchServices.cs Tests/ConWatch.Tests/ConWatchFactory.cs Tests/ConEngine.Tests/DohDetectorTests.cs Tests/ConPersistence.Tests/ConfigHealthTests.cs
git commit -m "feat(health): dns-over-https — Windows' per-interface DoH settings make the port-53 check advisory"
```

---

### Task 6: Docs

**Files:**
- Modify: `README.md` (Web UI section, First run section), `docs/superpowers/specs/2026-10-10-dns-watch-design.md` (§5.1 REST line)

- [ ] **Step 1: README**

In the "Web UI" section's page list add a bullet: `- **Config → DNS**: the resolvers your machine uses (switch any off), the public resolver asked directly, and the names they are asked for.` In "First run", after the anchors sentence, add: `It also asks which public resolver you use, so ConnectionWatcher can tell your resolver from everyone's.` Do not touch the top of the file (another branch is rewriting it).

- [ ] **Step 2: Spec**

In §5.1 replace the "Admin-locked REST `/api/v1/config/dns`" bullet with: `- The DNS page (§5.3) writes through the existing admin-locked endpoints — `PUT /api/v1/settings` for `Dns.Enabled`/`Dns.LookupNames`, `/api/v1/targets` for resolvers; no DNS-specific routes (ruling 2026-10-15, YAGNI). `ApiSurfaceLockTests` is unchanged.`

- [ ] **Step 3: Run everything and commit**

Run: `dotnet test ConnectionWatcher.sln` and `npm --prefix Programs/ConWatch/ClientApp test -- --run`
Expected: PASS.

```bash
git add README.md docs/superpowers/specs/2026-10-10-dns-watch-design.md
git commit -m "docs: DNS page and wizard step in the README; spec drops the dedicated DNS routes"
```

---

## Self-review notes

- **Spec coverage (step 3):** §5.2 banner line → Task 1; section → Task 2; §5.3 panel → Task 3; wizard and catalog unification (R3) → Task 4; `dns-over-https` → Task 5; §5.1 REST → Task 6 (ruled out). Parked Plan 2 items: M3 → the "re-checking" heading (Task 2); M5 → disabled resolvers listed from config (Task 3).
- **Type consistency:** `DnsStatus` fields match Plan 2's `StatusDns` JSON (`watched, state, systemResolvers, upstreamResolvers, blame, lookupNames`); `slabFor(verdict, dns?)` is used by the banner and both tests; `DnsModel` properties are the ones the page reads; `SetupTarget.LookupName` and `SetupApplyRequest.UpstreamResolverAddress` are used by the endpoint test and the script; `IDohDetector` (ConCore) is consumed by `ConfigHealth`, the host's DI, and the factory; the Windows reader (ConInfrastructure) is only ever named by `DohConfiguration.ForThisPlatform()` and its own test.
- **Review Focus:** 1 → Tasks 1, 2; 2 → Task 3; 3 → Task 3; 4 → Task 4; 5 → Task 5.
