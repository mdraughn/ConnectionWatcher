# Name Resolution Watch (DnsDown) — Design

**Date:** 2026-10-10, re-evaluated 2026-10-13 against master `2dee66e`
**Status:** Approved in conversation 2026-10-10; revised 2026-10-13 (§2.1) for Mark's review
**Builds on:** the 2026-10-09 evidence-based verdict design, all of whose plans are now merged: A (anchor-first verdict, own-evidence Unreachable), B (model warnings / notes), C (critical targets), D (clock-aligned loss windows, `ProbeOnlyLoss`, the probe-only veto). Plan D2 (paired-probe veto) is parked and does not touch this design.

## 1. The problem

"Is the internet down?" often has the answer "sort of": anything addressed by IP works, anything addressed by name does not. The usual cause is name resolution. The resolver this household uses (the router's forwarder, a Pi-hole, the ISP's resolver) has stopped answering, or is misconfigured, or something upstream of it is broken.

Today ConnectionWatcher cannot see this. Every anchor and path hop is probed by ICMP or TCP to an IP literal. The default anchors *are* public DNS servers, but nobody ever asks them a DNS question. Hostname targets re-resolve every five minutes, and after first activation a failed refresh keeps the old address, so a resolver outage is invisible to the engine. A dashboard that says **Your internet is UP** while every named thing in the house is failing is a failed product moment.

## 2. Decisions (Mark, 2026-10-10)

| # | Decision |
|---|---|
| D1 | **Measure the way this host resolves.** The evidence is the host's own configured resolvers, queried directly. We assume the rest of the LAN resolves the same way (the DHCP default). We cannot verify that and do not try. |
| D2 | **The admin may name their public resolver.** An optional "upstream" resolver (1.1.1.1, 8.8.8.8, …) queried directly lets us tell *your resolver is down* from *DNS is down*. |
| D3 | **A new headline verdict, `DnsDown`.** When the anchors prove the internet reachable but name resolution has failed, the banner says so. It is a verdict, not a note, and it reaches the tray, webhooks and MQTT like any verdict. Folding it into `LinkDegraded`/`SiteIncident` would lie about what is degraded; a second status field would be missed by every existing consumer. |
| D4 | **One state, varying blame.** `DnsDown` carries a blame line the way `LinkDown` names a hop: "your resolver" versus "no resolver". Two separate states were rejected; consumers that want the distinction read the structured `dns` block. |
| D5 | **Resolvers are targets** with a new scope, so the engine's thresholds, bursts, history, per-target MQTT sensors and reply counters all apply unchanged. **In the UI they are segregated**: a "Name resolution" section on the dashboard and a "DNS" panel in config, never rows in the tree or the Targets table. API and MCP users see the truth. |
| D6 | **One answering system resolver is enough.** The OS fails over between its configured resolvers, so the person's DNS works if any of them answers. `DnsDown` needs *every* system resolver Down. |
| D7 | **Create the system resolver targets on upgrade**, enabled. A read-only probe of the host's own resolver is what an upgrading user expects to get. The named upstream is only ever added by the wizard or the admin, because we cannot guess it. |
| D8 | **Lookup names are seeded into configuration**, visible and editable, so the README's "only the traffic you configured" promise stays literally true. |
| D9 | **MQTT internet binary goes OFF for `DnsDown`.** From the person's chair the internet is down. A new `dns` binary sensor carries the distinction for automations that care. |
| D10 | **A managed resolver that is gone from the OS list is deleted, not kept** (2026-10-14). Its history rows stay under its old id, as for any deleted target. A roaming laptop no longer piles up disabled resolvers. The watch being turned off still only disables present resolvers. |
| D11 | **A filtering resolver is a distinct `Filtered` state, not a lying one** (2026-10-14). Pi-hole / AdGuard blocklists answer the seeded names with `0.0.0.0`, `::`, `127.0.0.1`, NXDOMAIN or an empty answer. That is a working resolver that blocks, so the sample is a hit with `Message` `Filtered: …`, the resolver stays `Responding`, `DnsDown` never fires for it, and the `dns` summary state is `Filtered` with a `ResolverFiltered` note. A real wrong address (a captive portal's IP) stays a `WrongAnswer` miss. |

Parked: asking DHCP for the LAN's DNS servers via DHCPINFORM (no lease taken). It needs a broadcast socket on port 68 and never crosses a Docker bridge, so it is a later refinement for the native Windows install and host-network NAS case, not a dependency.

### 2.1 Revisions from the 2026-10-13 re-evaluation (for Mark to confirm)

Plans C and D landed between the first draft and now. Four points changed; the rest of the edits below only name the code as it is today.

| # | Change | Why |
|---|---|---|
| R1 | **Resolver targets have no parent.** The first draft hung them under the router so a Down router would explain them as `Unreachable`. Dropped: the household resolver usually *is* the router's address, so a DNS answer from it must never count as proof that the router forwards (the verdict's frontier rule and the `HopBypassed`/`NeverAnswered` witness rule both take any answering descendant as proof). With no parent they are never a witness and never `Unreachable`; when the anchors are down the verdict is `LinkDown` anyway, because reachability is decided before DNS (§4.4). The validator forbids a parent on a Resolver target, as it does for Local. |
| R2 | **Resolver samples stay out of the loss windows, the corroboration sweep and glitches**, like Local. Loss attribution compares hops with what lies beyond them; a parentless resolver has no place in it, and a DNS timeout has a different rhythm from an ICMP loss. The resolver still bursts on its own first miss, because that burst is the target's own `PingDiscipline`, not the sweep. |
| R3 | **Seeded names are the four anchor operators' own names**: `dns.google`, `one.one.one.one`, `dns.quad9.net`, `dns.opendns.com`. They have stable, published answer sets, which is what the "lying resolver" check needs; `example.com` and `quad9.net` do not. The seed list and the wizard's anchor catalog are the same table. |
| R4 | **`DnsNotWatched` and `DnsOverHttps` are config-health warnings, not notes.** The model check evaluates monitor states; these two are configuration facts, like `no-upstream-resolver`. The four observational notes stay in the model check. |

## 3. Goals and non-goals

**Goals**
- A dead or misconfigured household resolver produces **Your internet is UP, but name lookups are failing** within about a minute, with a blame line that says whether the problem is the household resolver or DNS as a whole.
- Home Assistant and webhook consumers learn about it through the channels they already use.
- Existing installs get the system-resolver check on upgrade without any other change.
- The admin can see each resolver's state and the lookup names, and tune or disable the check.

**Non-goals**
- Detecting that *other* LAN devices use a different resolver than this host (D1).
- Specific names failing while the resolver works (an authoritative outage). The `NameUnresolvable` note is the closest we get.
- DNS-over-HTTPS / DNS-over-TLS probing. On Windows 11 with DoH configured for a resolver, the probe measures the server over port 53, not the OS's actual path; a config-health warning says so.
- Diagnosing *why* the resolver is down. We name it; we do not fix it.
- Changing how hostname targets are resolved (the engine keeps using the OS API for that).
- Loss attribution for resolvers (R2). DNS evidence never feeds `LossWindows`, `ProbeOnlyLoss` or the probe-only veto.

## 4. Design — engine

### 4.1 Model

- **`TargetScope.Resolver`** (= 2) beside `Verdict` and `Local`. Scope already decides tree membership and verdict influence. Resolver targets are outside the tree (no `ParentId`, R1) and feed only the DNS rule (§4.4). `WatcherOptionsValidator` rejects a parent on a Resolver target.
- **`ProbeKind.Dns`**, probe string `dns`, no port (53 implied). `ProbePolicy` allows it for every address, public or private. The engine's per-kind burst cap (`WatcherEngine`, today 1000 ms ICMP / 2500 ms TCP) gets a DNS value of 2000 ms.
- **`Target.SystemManaged`** (bool, default false). True means the engine owns the lifecycle: the OS resolver list created it and may retire it. The admin can **disable** it but cannot edit its address or delete it. Retired targets are kept **disabled** with their history and are re-enabled if the address returns to the OS list.
- **`Critical`** is honoured on a Resolver target like any other (Plan C), but the DNS panel does not expose it: `DnsDown` already reports the household resolver, and an upstream resolver going quiet is a note, not an alarm. System-managed targets are created with `Critical = false`.
- **`PingProfile.Resolver`** preset: `NormalInterval` 30 s, `PingTimeout` 5 s, `BurstInterval` 2 s, `BurstMaxDuration` 60 s, `HistorySize` 30, `MinUsableSample` 6, Local-style thresholds (Responding 0.80, Faltering 0.50, Down 0.10, Recovering 0.25). One slow query is not a Faltering; a dead resolver is Down in about a minute from the first miss, because that miss starts the target's own burst.
- **Settings keys** (the settings table, seeded through `SettingsDefaults` on first start and on upgrade): `Dns.Enabled` (default `true`), `Dns.LookupNames` (a JSON array of name + optional expected addresses, seeded with the four names of R3), and `Dns.RetiredResolvers` (per-node; the addresses the system-resolver service itself disabled, so it may re-enable them and nothing else).
- EF migration **`AddDnsWatch`**: `Targets.SystemManaged` (default false). The settings need no migration.

### 4.2 The probe

- **`DnsQueryTransceiver`** in `ConInfrastructure` implements the existing `IPingTransceiver`, and `PingTransceiverFactoryIcmp.Create` returns it for `ProbeKind.Dns` the way it returns `TcpConnectTransceiver` for TCP. No new prober interface: the engine's `PingController`, `TargetMonitor`, thresholds and bursts see DNS samples as ordinary `PingResult`s, with the query time as `RoundtripTime` and the outcome as `Message`.
- One query per sample: a hand-built DNS message (12-byte header, one question, type A, class IN, recursion desired) over UDP to port 53 of the target's address, with the profile's timeout. The parser checks the ID, QR and RCODE, and walks the answer section counting A records. Roughly 150 lines; no new dependency. Truncated or malformed replies are misses with `Message` "malformed answer". The ID is random per query and the reply must match it.
- **Name rotation.** The factory takes an `IDnsLookupNames` provider that the engine fills from `DnsOptions.LookupNames` plus the hostnames of every enabled hostname target. Rotation is per resolver target, round-robin, so one dead authoritative zone cannot fail a resolver on its own.
- **Hit and miss.**
  - A seeded name carries **expected addresses**. A hit is NOERROR with at least one A record in the expected set, `Message` `Verified (dns.google)`; an admin or hostname name's hit stays `Answered (name)`, and only seeded-name outcomes (`Verified`, `Filtered`, `WrongAnswer`) are judged by the notes and the `Filtered` state. A **blocked** answer (D11: every address is `0.0.0.0`, `::` or `127.0.0.1`, or NXDOMAIN, or an empty NOERROR answer) is a **hit** with `Message` `Filtered: NXDOMAIN` / `Filtered: 0.0.0.0`: the resolver works, it filters. Any other address outside the set is a **miss** with `Message` `WrongAnswer: 10.0.0.1`.
  - An admin-added name without expected addresses, and a hostname-target name: any real answer (NOERROR with or without records, NXDOMAIN) is a hit. We cannot know the right answer.
  - Timeout, SERVFAIL, REFUSED, and malformed replies are misses.
- **`DnsResultLog`** in the engine subscribes to `PingSampleSource` and keeps the last 20 results per Resolver target (name asked, hit or miss, `Message`). The notes (§4.5) and the status `lookupNames[]` read it. It is in-memory only, like the loss windows.
- In the engine's result pump, the `Scope != Local` guard that keeps samples out of the suspicion coordinator and `LossWindows` becomes `Scope == Verdict` (R2).

### 4.3 The OS resolver list

- New **`ISystemResolverSource`** / `SystemResolverSource` in `ConInfrastructure`:
  - Windows: the `DnsAddresses` of the interface(s) that own a default route (`GetIPProperties()`, same pattern as `GatewayDiscovery`), in order, IPv4 first.
  - Linux: `nameserver` lines of `/etc/resolv.conf`, in order.
  - Returns an ordered, de-duplicated list of addresses, plus a flag per address when it is Docker's embedded resolver (`127.0.0.11`).
- A host-side **`SystemResolverService`** (hosted after `WatcherHostService`, so the config is seeded) reconciles the list against the `SystemManaged` targets: at startup, every five minutes (the hostname refresh cadence), on `NetworkChange.NetworkAddressChanged`, and when a `Dns.*` setting changes. The engine itself is rebuilt from stored config on every change, so the reconcile lives in the host, not the engine. New address → create a Resolver-scope target, `Probe = dns`, profile `Resolver`, `SystemManaged = true`, no parent, name "Your resolver 10.1.10.1" (or "Docker's resolver" for `127.0.0.11`). Address gone → **delete** the target (D10; the next appearance creates a fresh one). Watch off → disable every present managed target and remember its address in `Dns.RetiredResolvers`; watch on → re-enable only an address the service retired; an admin-disabled system resolver stays disabled. Every change goes through `ConfigService.EditDocumentAsync`, so the engine rebuilds the way it does for an edit.
- The upstream resolver(s) are ordinary Resolver-scope targets with `SystemManaged = false`, named by the admin or wizard ("1.1.1.1 directly").

### 4.4 The verdict

The coordinator today: `Classify` decides reachability first (one counted anchor answering), waits under `AnchorWaitCap` when none answers but one could, then `Blame`s `LinkDown`; when reachable it calls `Quality` for Healthy / SiteIncident / LinkDegraded / InternetWeather, where a holding `ProbeOnlyLoss` note vetoes blaming a Faltering hop. `Current` is a `VerdictSnapshot(State, Detail, Operator)`.

- **`VerdictState.DnsDown`**, ranked above `Healthy`, `SiteIncident`, `InternetWeather` and `LinkDegraded`, below `LinkDown`. In `Classify` the DNS check sits **between reachability and `Quality`**: once a counted anchor answers, DNS is checked, and only if DNS is fine does `Quality` run. When the anchors say the link is down, DNS failing says nothing new and is folded into the `LinkDown` detail ("name lookups are failing too").
- **Rule.** `DnsDown` when all of:
  1. a counted anchor is answering (reachability proven);
  2. `Dns.Enabled` and the combined lookup list is non-empty;
  3. at least one enabled system resolver is determined;
  4. **every** enabled system resolver is `Down`.
  `Starting`, `Faltering` and `Recovering` resolvers do not count as Down. While some system resolvers are undetermined and the rest are Down, the coordinator waits under the same `AnchorWaitCap` (30 s) before concluding, but the verdict stays whatever the anchors prove (`Healthy`, `SiteIncident`, …) during that wait: a resolver wait only delays `DnsDown`, it never turns a reachable internet into `Unknown` (ruling 2026-10-15). The anchor wait and the resolver wait keep separate clocks.
- **Construction.** The coordinator receives the Resolver-scope monitors (system-managed and upstream, from the monitors dictionary it already gets) and a `DnsWatchEnabled` flag. Resolver transitions already trigger `Recalculate`, because `Start()` subscribes to every monitor.
- **`VerdictSnapshot` gains `Dns`** (a `DnsBlame?` record: `Kind`, `Message`, `Resolvers[]` with address and last `Message`). `Operator` is null for `DnsDown`. `Map(DnsDown)` is `HostState.Down`, so the verdict transition reads as an outage to anything that keys on the mapped state.
- **Blame**, decided from the upstream resolvers:
  - any upstream resolver answering → kind `YourResolver`: "Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly."
  - every upstream resolver Down, or none configured → kind `NoResolver`: "No resolver is answering, though the internet is reachable." When none is configured the detail adds "Name a public resolver in Config → DNS so ConnectionWatcher can tell your resolver from everyone's."
- **Interaction with the path (R1).** Resolvers have no path, so they are never a frontier witness, never `Unreachable`, never blamed by `Quality`'s shallowest-losing-hop rule, and never subject to the probe-only veto. Router dead: anchors Down within the burst, `LinkDown` blamed on the router, the resolver's own Down is just one more Down target.
- **Degraded, not down.** The `dns` summary state (§5.1) is `Degraded` when some system resolver is `Faltering`/`Recovering`, or some but not all are Down. The verdict is unchanged by `Degraded`.
- **Filtered, not down (D11).** The `dns` summary state is `Filtered` when every answering system resolver's recent results for the seeded names are `Filtered: …` (at least 3 of the last 10 seeded-name asks per resolver, none `Answered`). The verdict is unchanged by `Filtered`; the `ResolverFiltered` note explains it.

### 4.5 Notes (admin-facing, never verdict)

Added to `ModelCheckCoordinator` as `ModelWarningKind` values with `ModelCondition.KeyFor` keys; same lifecycle, acknowledge, badge, panel and tray treatment as the existing notes. `ProbeText` learns "dns". `TrayStatus` and the client's `ModelWarning.kind` union gain the four kinds. All four re-prove within the ledger's standard `ResyncGrace`, so no per-kind grace like `ProbeOnlyLoss` is needed. Resolvers have no descendants, so `HopBypassed` and `NeverAnswered` never fire on them.

| Kind | Key | Condition | Message (sketch) |
|---|---|---|---|
| `ResolverPartial` | `resolver-partial:{id}` | Some but not all enabled system resolvers are Down | "Your secondary resolver 10.1.10.2 isn't answering; 10.1.10.1 is." |
| `ResolverLying` | `resolver-lying:{id}` | In the `DnsResultLog`, 3 of the last 10 results for this resolver are `WrongAnswer: …` | "Your resolver answered dns.google with 10.0.0.1 — a captive portal or interception?" |
| `ResolverFiltered` | `resolver-filtered:{id}` | In the `DnsResultLog`, 3 of the last 10 seeded-name results for this resolver are `Filtered: …` and none is `Answered` | "Your resolver blocks dns.google (answered 0.0.0.0) — a Pi-hole or AdGuard blocklist? Name resolution still works; the seeded lookup names can't prove it. Add a lookup name your resolver allows." |
| `UpstreamResolverDown` | `upstream-resolver-down:{id}` | A system resolver answers but this upstream resolver is Down | "1.1.1.1 isn't answering direct queries; your resolver is fine." |
| `NameUnresolvable` | `name-unresolvable` (no target) | One lookup name missed its last 3 asks on every answering resolver while other names hit | "example.com isn't resolving anywhere." |

**Config-health warnings** (`ConfigHealth`, settings-page banner, dismissable), codes in the existing kebab style:

| Code | Condition |
|---|---|
| `no-upstream-resolver` | DNS watch enabled and no upstream resolver configured. "Name a public resolver so ConnectionWatcher can tell your resolver from everyone's." |
| `dns-not-watched` | `Dns.Enabled` is false, the lookup list is empty, or no system resolver was found. |
| `dns-over-https` | Windows reports DoH configured for a system resolver address. "Windows resolves through DNS-over-HTTPS for 1.1.1.1; the check measures port 53." |

## 5. Design — surfaces

### 5.1 Status, API, MCP

- `StatusSnapshot` (built by `StatusSnapshotBuilder`, served by `/api/v1/status`) gains a **`dns`** block:
  ```json
  {
    "watched": true,
    "state": "Ok | Degraded | Filtered | Down | Unknown",
    "systemResolvers": [{ "id": "...", "address": "10.1.10.1", "label": "Your resolver 10.1.10.1", "state": "Responding", "detail": null, "enabled": true }],
    "upstreamResolvers": [{ "id": "...", "address": "1.1.1.1", "label": "1.1.1.1 directly", "state": "Responding", "detail": null }],
    "blame": { "kind": "YourResolver | NoResolver", "message": "...", "resolvers": ["10.1.10.1"] },
    "lookupNames": [{ "name": "dns.google", "lastResult": "ok | miss", "detail": null }]
  }
  ```
  Resolver targets also stay in `targets[]`, where `TargetStatus` already carries `scope`, `critical` and `explainedBy`; they add `probe: "dns"` and `systemManaged`.
- MCP `get_current_status` returns the same `dns` block. Tool descriptions and the README's MCP section mention it.
- **Admin-locked REST** `/api/v1/config/dns`: GET/PUT the section (`enabled`, `lookupNames`), POST/DELETE upstream resolvers, PUT `systemResolvers/{id}/enabled`. `ApiSurfaceLockTests` gains the routes. Status stays read-only on the open surface, so SECURITY.md is unchanged.
- **Metrics**: `conwatch_dns_up` gauge (1 = Ok/Degraded, 0 = Down, absent when not watched). `MetricsEndpoint` treats Resolver-scope targets as it treats Verdict-scope ones for the per-target series (only Local is set aside today).
- **History**: `HistoryQueries` and `TransitionDto.Scope` split transitions into Local and not-Local; Resolver transitions stay on the internet side, and `TransitionDto.Scope` reads the real scope for a Resolver target rather than defaulting to Verdict.
- **ISP report**: `BuildTargetRowsAsync` already lists only Verdict-scope targets, so resolvers get no row. The verdict totals are keyed by verdict name (`LinkDown`, `LinkDegraded`, `InternetWeather`); `DnsDown` time gets its **own** "Name resolution" line and is never added to the ISP's totals, and the verdict subject's outage counter ignores `DnsDown` transitions even though they map to `Down`. A household resolver failure is not the ISP's outage.
- **Export, import, clone**: upstream resolvers and the `Dns` section travel; system-managed resolvers, their transitions and rollups are always dropped (`CloneService.WithoutLocalDevices` pattern, unconditional), because the destination regenerates them from its own OS.

### 5.2 Dashboard

- `VerdictBanner` gains `DnsDown`: headline **Your internet is UP, but name lookups are failing**, the blame message beneath, same red as `LinkDown`. `LinkDown` gains an extra line "Name lookups are failing too" when the `dns` state is Down.
- A **"Name resolution"** section under the tree, beside "How's my LAN" (`LanSection` is the template): one line per resolver (label, state chip, last detail when not Responding), and a line per lookup name **only** when that name has a problem. When the section is disabled or empty, one line: "Name resolution isn't being watched." The tree, `RibbonBoard` and the Razor fallback page (`Index.cshtml.cs`, which already sets Local monitors aside) exclude Resolver-scope targets.
- The header notes badge and panel show the new note kinds without change.

### 5.3 Config

- A **"DNS"** panel: Enabled toggle; the system resolvers as read-only rows with a per-row disable switch and a "found from your network settings" caption; the upstream resolver list with add (address, name) and remove; the lookup names list with add and remove and a note that seeded names are checked against known answers. The Targets table excludes Resolver-scope targets.
- **Setup wizard**: `SetupApplyRequest` gains `UpstreamResolverAddress`. A step after anchors, "Which public resolver do you use?", offers the `AnchorCatalog` entries (Google, Cloudflare, Quad9, OpenDNS) plus "I don't know", which picks the first chosen anchor. The catalog entries gain their lookup name and expected addresses, and that same table is the seed for `LookupNames` (R3). One sentence of why: "This lets ConnectionWatcher tell your resolver from everyone's."
- Config health: the three codes of §4.5.

### 5.4 Tray, webhooks, MQTT

- **Tray**: `TrayStatus` maps `DnsDown` to `TrayState.Down` (red), tooltip "Internet: name lookups are failing", toast on entry ("Your internet is up, but name lookups are failing — your resolver 10.1.10.1 isn't answering") and on exit, like `LinkDown`.
- **Webhooks**: `DnsDown` passes the "verdict only" filter. The verdict payload gains `dns` (the blame block). Resolver **target** transitions are filtered the way Local ones are (`WebhookNotifier.Attach` keeps only critical or non-side subjects; `IsLocalSubject` generalises to "not Verdict scope"), because the verdict already carries the fact.
- **MQTT**: `HaDiscovery.InternetPayload` returns OFF for `DnsDown` as well as `LinkDown`. New `dns` **binary sensor** with Home Assistant discovery (`{base}_dns`, device class `connectivity`), ON for Ok/Degraded, OFF for Down, unavailable when not watched; attributes carry `state`, `blame`, and the resolver lists. Verdict attributes gain `dnsBlame`. Per-target sensors already publish every target with `critical` and `scope` attributes, so resolvers appear with scope `Resolver` for free.

### 5.5 Upgrade and install

- The migration seeds the lookup names and `Enabled = true`. On first start the reconcile loop creates the system resolver targets. No other target changes. `no-upstream-resolver` shows once.
- The Docker image needs no compose change; the container's resolver is what is measured and is labelled "Docker's resolver".
- README: the privacy section lists the DNS lookups and the seeded names; a "Name resolution" subsection under "How the verdict is decided" explains the rule and the blame line.

## 6. Edge cases

- **Router's forwarder dies, upstream fine.** System resolver Down in about a minute; anchors answer; `DnsDown`, blame `YourResolver`. Tray red, HA internet OFF, `dns` OFF.
- **ISP intercepts or blocks port 53.** System and upstream resolvers both Down; `DnsDown`, blame `NoResolver`.
- **Link down.** Everything Down; `LinkDown` with "name lookups are failing too". Never `DnsDown`, because reachability is decided first.
- **Router dead, resolver is the router.** Anchors Down within the burst; `LinkDown` blamed on the router. The resolver's Down is its own transition, filtered from webhooks like a Local target's, and does not become a second incident. It is never a witness for the router (R1).
- **Two system resolvers, one dead.** `Healthy`, `ResolverPartial` note, `dns` state Degraded.
- **Pi-hole / AdGuard blocks the seeded names** (DoH-bypass blocklists block exactly `dns.google`, `one.one.one.one`, …). Each answer is `0.0.0.0` or NXDOMAIN: a `Filtered: …` hit (D11). The resolver stays `Responding`, the `dns` state is `Filtered`, `ResolverFiltered` explains it and suggests adding an allowed lookup name. Never `DnsDown`.
- **Captive portal.** Every seeded name answers with the portal's real address: `WrongAnswer` misses on all, `DnsDown` with `ResolverLying` explaining it. (A portal that answers `0.0.0.0` instead reads as `Filtered`; the anchors are down behind a portal anyway, so the verdict is `LinkDown`.)
- **Lookup list emptied and no hostname targets.** Resolvers idle, `dns-not-watched`, `dns.watched = false`, binary sensor unavailable.
- **DHCP renew changes the resolver.** The network-change event triggers a reconcile: the old target is deleted (its history rows stay under its id), a new one is created and burst.
- **Windows DoH.** Probe still runs over port 53; `dns-over-https` explains the gap.
- **Warm start.** Resolver targets are seeded like any other; a seeded Down resolver re-proves on its own evidence. A seeded `DnsDown` verdict is adopted silently like any seeded verdict and reclassified once at start.
- **Engine rebuild.** `DnsResultLog` and the ledger's standard grace handle the notes; no DNS note takes longer than a minute to re-prove.

## 7. Testing

- **ConInfrastructure**: DNS message builder and parser against fixed byte arrays (NOERROR with A records, NOERROR empty, NXDOMAIN, SERVFAIL, REFUSED, truncated, ID mismatch, garbage); expected-answer matching for seeded names; `SystemResolverSource` against a fake `resolv.conf` and a fake interface list, including the Docker address flag; the factory returns `DnsQueryTransceiver` for `dns`.
- **ConEngine** (fake transceivers, fake time, no sleeps): every §6 case that touches the verdict, including "resolver Down is never a frontier witness" and "resolver samples never reach `LossWindows` or the suspicion coordinator"; reconcile on a changed list (create, retire, re-enable, admin-disabled stays disabled); name rotation per resolver; the 30 s wait; `Degraded` never changes the verdict; note start/end for each new kind; the validator rejects a parent on a Resolver target.
- **Persistence**: `AddDnsWatch` migration on an existing database; export/import/clone round-trip (upstream resolvers and the Dns section travel, system-managed ones do not); `TransitionDto.Scope` for a Resolver target.
- **API/MCP**: the `dns` block shape; locked config routes; `ApiSurfaceLockTests`.
- **Report**: `DnsDown` time on its own line and absent from the ISP totals and the outage count.
- **Notifiers**: `InternetPayload(DnsDown) == OFF`; `dns` binary discovery and state payloads; webhook verdict-only filter passes `DnsDown` and drops resolver target transitions; `TrayStatus` mapping, note lines and toast text.
- **UI (vitest)**: banner case; Name resolution section (watched, degraded, down, not watched); DNS config panel; wizard step including "I don't know".

## 8. Rollout

Each step releasable on `edge`:

1. **Plumbing.** `ProbeKind.Dns`, `DnsQueryTransceiver`, `TargetScope.Resolver` (validator, pump guard), `SystemManaged`, `PingProfile.Resolver`, `DnsOptions`, migration, `SystemResolverSource`, reconcile loop, `DnsResultLog`. Verdict unchanged; resolvers visible only in `targets[]`.
2. **Verdict.** `DnsDown`, blame, the `dns` status block, the four notes, the three config-health codes, metrics, the ISP report line.
3. **UI.** Banner, Name resolution section, DNS config panel, wizard step, README.
4. **Consumers.** Tray, webhooks, MQTT binary and sensor.
