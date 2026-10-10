# Name Resolution Watch (DnsDown) — Design

**Date:** 2026-10-10
**Status:** Approved in conversation 2026-10-10; written spec for review
**Builds on:** 2026-10-09 evidence-based verdict design (anchor-first verdict, model warnings / notes, critical targets)

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
| D5 | **Resolvers are targets** with a new scope, so the engine's thresholds, bursts, history, per-target MQTT sensors and root-cause rules all apply unchanged. **In the UI they are segregated**: a "Name resolution" section on the dashboard and a "DNS" panel in config, never rows in the tree or the Targets table. API and MCP users see the truth. |
| D6 | **One answering system resolver is enough.** The OS fails over between its configured resolvers, so the person's DNS works if any of them answers. `DnsDown` needs *every* system resolver Down. |
| D7 | **Create the system resolver targets on upgrade**, enabled. A read-only probe of the host's own resolver is what an upgrading user expects to get. The named upstream is only ever added by the wizard or the admin, because we cannot guess it. |
| D8 | **Lookup names are seeded into configuration**, visible and editable, so the README's "only the traffic you configured" promise stays literally true. |
| D9 | **MQTT internet binary goes OFF for `DnsDown`.** From the person's chair the internet is down. A new `dns` binary sensor carries the distinction for automations that care. |

Parked: asking DHCP for the LAN's DNS servers via DHCPINFORM (no lease taken). It needs a broadcast socket on port 68 and never crosses a Docker bridge, so it is a later refinement for the native Windows install and host-network NAS case, not a dependency.

## 3. Goals and non-goals

**Goals**
- A dead or misconfigured household resolver produces **Your internet is UP, but name lookups are failing** within about a minute, with a blame line that says whether the problem is the household resolver or DNS as a whole.
- Home Assistant and webhook consumers learn about it through the channels they already use.
- Existing installs get the system-resolver check on upgrade without any other change.
- The admin can see each resolver's state and the lookup names, and tune or disable the check.

**Non-goals**
- Detecting that *other* LAN devices use a different resolver than this host (D1).
- Specific names failing while the resolver works (an authoritative outage). The `NameUnresolvable` note is the closest we get.
- DNS-over-HTTPS / DNS-over-TLS probing. On Windows 11 with DoH configured for a resolver, the probe measures the server over port 53, not the OS's actual path; a config-health note says so.
- Diagnosing *why* the resolver is down. We name it; we do not fix it.
- Changing how hostname targets are resolved (the engine keeps using the OS API for that).

## 4. Design — engine

### 4.1 Model

- **`TargetScope.Resolver`** (= 2) beside `Verdict` and `Local`. Scope already decides tree membership and verdict influence; Resolver targets are outside the tree and feed only the DNS rule (§4.4). They may still have a `ParentId` (set by the engine to the router when one is known, or left null), so the root-cause rule can explain them as `Unreachable` behind a Down hop.
- **`ProbeKind.Dns`**, probe string `dns`, no port (53 implied). `ProbePolicy` allows it for every address, public or private.
- **`Target.SystemManaged`** (bool, default false). True means the engine owns the lifecycle: the OS resolver list created it and may retire it. The admin can **disable** it but cannot edit its address or delete it. Retired targets are kept **disabled** with their history and are re-enabled if the address returns to the OS list.
- **`PingProfile.Resolver`** preset: `NormalInterval` 30 s, `PingTimeout` 5 s, `BurstInterval` 2 s, `BurstMaxDuration` 60 s, `HistorySize` 30, `MinUsableSample` 6, Local-style thresholds (Responding 0.80, Faltering 0.50, Down 0.10, Recovering 0.25). One slow query is not a Faltering; a dead resolver is Down in about a minute from the first miss (burst).
- **`DnsOptions`** configuration section: `Enabled` (default true) and `LookupNames` (list). The migration seeds `dns.google`, `one.one.one.one`, `example.com`, `quad9.net`.
- EF migration **`AddDnsWatch`**: `Targets.SystemManaged` (default false), the `Dns` settings rows with the seeded names and `Enabled = true`.

### 4.2 The probe

- New **`IDnsProber`** in `ConInfrastructure` (factory like the ping and TCP transceivers). One query per sample: a hand-built DNS message (12-byte header, one question, type A, class IN, recursion desired) over UDP to port 53 of the target's address, with the profile's timeout. The parser reads the header (ID match, QR, RCODE) and walks the answer section counting A records. Roughly 150 lines; no new dependency. Truncated or malformed replies are misses with detail "malformed answer". The ID is random per query and the reply must match it.
- **Name rotation.** The prober asks the next name from the combined list: the seeded/admin `LookupNames` plus the hostnames of every enabled hostname target. Rotation is per resolver target, round-robin, so one dead authoritative zone cannot fail a resolver on its own.
- **Hit and miss.**
  - Seeded names (`dns.google`, `one.one.one.one`, `example.com`, `quad9.net`) carry **expected answers** shipped with the app (the well-known address sets). A hit is NOERROR with at least one A record in the expected set. NXDOMAIN, an empty answer, or an address outside the set is a **miss** with detail "wrong answer: 10.0.0.1", which also feeds the `ResolverLying` note.
  - Admin-added names and hostname-target names: any real answer (NOERROR with or without records, NXDOMAIN) is a hit. We cannot know the right answer.
  - Timeout, SERVFAIL, REFUSED, and malformed replies are misses.
- The prober produces an ordinary ping sample (success, RTT = query time, detail text). `TargetMonitor`, thresholds, `PingDiscipline` bursts and history see it as any other sample. The `SuspicionCoordinator` bursts a resolver on its first miss like any target.

### 4.3 The OS resolver list

- New **`ISystemResolverSource`** / `SystemResolverSource` in `ConInfrastructure`:
  - Windows: the `DnsAddresses` of the interface(s) that own a default route (`GetIPProperties()`, same pattern as `GatewayDiscovery`), in order, IPv4 first.
  - Linux: `nameserver` lines of `/etc/resolv.conf`, in order.
  - Returns an ordered, de-duplicated list of addresses, plus a flag per address when it is Docker's embedded resolver (`127.0.0.11`).
- **`WatcherEngine` reconciles** the list against the `SystemManaged` targets: at startup, every five minutes (the hostname refresh cadence), and on `NetworkChange.NetworkAddressChanged`. New address → create a Resolver-scope target, `Probe = dns`, profile `Resolver`, `SystemManaged = true`, name "Your resolver 10.1.10.1" (or "Docker's resolver" for `127.0.0.11`), parent = the router target if the config has one. Address gone → disable the target (`retired`). Address back → re-enable. An admin-disabled system resolver stays disabled across reconciles (the engine records which disables were its own).
- The upstream resolver(s) are ordinary Resolver-scope targets with `SystemManaged = false`, named by the admin or wizard ("1.1.1.1 directly").

### 4.4 The verdict

- **`VerdictState.DnsDown`**, ranked above `Healthy`, `SiteIncident`, `InternetWeather` and `LinkDegraded`, below `LinkDown`. When the anchors say the link is down, DNS failing says nothing new and is folded into the explanation ("name lookups are failing too").
- **Rule.** `DnsDown` when all of:
  1. the anchors prove up by the existing one-counted-anchor rule (§4.1 of the 2026-10-09 spec);
  2. `Dns.Enabled` and the combined lookup list is non-empty;
  3. at least one enabled system resolver is determined;
  4. **every** enabled system resolver is `Down` (or `Unreachable`).
  `Starting`, `Faltering` and `Recovering` resolvers do not count as Down. While some system resolvers are undetermined and the rest are Down, the same 30 s wait cap (`AnchorWaitCap`) applies before concluding.
- **Blame** (the `DnsBlame` record on the verdict), decided from the upstream resolvers:
  - any upstream resolver answering → kind `YourResolver`: "Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly."
  - every upstream resolver Down, or none configured → kind `NoResolver`: "No resolver is answering, though the internet is reachable." When none is configured the detail adds "Name a public resolver in Config → DNS so ConnectionWatcher can tell your resolver from everyone's."
  - The blame carries the system resolver addresses and each one's last failure detail.
- **Interaction with the path.** A system resolver whose `ParentId` is a Down hop without proven forwarding is `Unreachable` by the existing root-cause rule, and in that situation the anchors are also down, so the verdict is `LinkDown` and never `DnsDown`. A resolver that is Down while its parent forwards is plain Down (its own problem), which is exactly the `DnsDown` case.
- **Degraded, not down.** The `dns` summary state (§5.1) is `Degraded` when some system resolver is `Faltering`/`Recovering`, or some but not all are Down. The verdict is unchanged by `Degraded`.

### 4.5 Notes (admin-facing, never verdict)

Added to `ModelCheckCoordinator` as warning kinds; same lifecycle, acknowledge, badge, panel and tray treatment as the existing notes.

| Kind | Condition | Message (sketch) |
|---|---|---|
| `ResolverPartial` | Some but not all enabled system resolvers are Down | "Your secondary resolver 10.1.10.2 isn't answering; 10.1.10.1 is." |
| `ResolverLying` | A seeded name got a wrong answer from a resolver in ≥ N of the last M samples | "Your resolver answered dns.google with 10.0.0.1 — a captive portal or interception?" |
| `UpstreamResolverDown` | A system resolver answers but an upstream resolver is Down | "1.1.1.1 isn't answering direct queries; your resolver is fine." |
| `NameUnresolvable` | One lookup name misses on every resolver while other names answer | "example.com isn't resolving anywhere." |
| `DnsNotWatched` | `Dns.Enabled` is false, the lookup list is empty, or no system resolver was found | "Name resolution isn't being watched." |
| `DnsOverHttps` | Windows reports DoH configured for a system resolver address | "Windows resolves through DNS-over-HTTPS for 1.1.1.1; the check measures port 53." |

`NoUpstreamResolver` ("Name a public resolver so ConnectionWatcher can tell your resolver from everyone's") is a **config-health** warning (settings page banner), not a note, because it is a configuration gap rather than an observation. It appears once after upgrade and is dismissable.

## 5. Design — surfaces

### 5.1 Status, API, MCP

- `/api/v1/status` gains a **`dns`** block:
  ```json
  {
    "watched": true,
    "state": "Ok | Degraded | Down | Unknown",
    "systemResolvers": [{ "id": "...", "address": "10.1.10.1", "label": "Your resolver 10.1.10.1", "state": "Responding", "detail": null, "enabled": true }],
    "upstreamResolvers": [{ "id": "...", "address": "1.1.1.1", "label": "1.1.1.1 directly", "state": "Responding", "detail": null }],
    "blame": { "kind": "YourResolver | NoResolver", "message": "...", "resolvers": ["10.1.10.1"] },
    "lookupNames": [{ "name": "dns.google", "lastResult": "ok | miss", "detail": null }]
  }
  ```
  Resolver targets also stay in `targets[]` with `scope: "Resolver"`, `probe: "dns"` and `systemManaged`.
- MCP `get_current_status` returns the same `dns` block. Tool descriptions and the README's MCP section mention it.
- **Admin-locked REST** `/api/v1/config/dns`: GET/PUT the section (`enabled`, `lookupNames`), POST/DELETE upstream resolvers, PUT `systemResolvers/{id}/enabled`. `ApiSurfaceLockTests` gains the routes. Status stays read-only on the open surface, so SECURITY.md is unchanged.
- Metrics: `conwatch_dns_up` gauge (1 = Ok/Degraded, 0 = Down, absent when not watched). Per-target series cover the resolvers already.
- Export, import and clone carry Resolver targets (upstream only; system-managed ones are regenerated on the destination) and the `Dns` section.

### 5.2 Dashboard

- `VerdictBanner` gains `DnsDown`: headline **Your internet is UP, but name lookups are failing**, the blame message beneath, same red as `LinkDown`. `LinkDown` gains an extra line "Name lookups are failing too" when the `dns` state is Down.
- A **"Name resolution"** section under the tree, beside "How's my LAN": one line per resolver (label, state chip, last detail when not Responding), and a line per lookup name **only** when that name has a problem. When the section is disabled or empty, one line: "Name resolution isn't being watched." The tree and `RibbonBoard` exclude Resolver-scope targets.
- The header notes badge and panel show the new note kinds without change.

### 5.3 Config

- A **"DNS"** panel: Enabled toggle; the system resolvers as read-only rows with a per-row disable switch and a "found from your network settings" caption; the upstream resolver list with add (address, name) and remove; the lookup names list with add and remove and a note that seeded names are checked against known answers. The Targets table excludes Resolver-scope targets.
- **Setup wizard**: a step after anchors, "Which public resolver do you use?", offering the anchors that are known public resolvers (1.1.1.1, 8.8.8.8, 9.9.9.9, …) plus "I don't know", which picks the first known one. One sentence of why: "This lets ConnectionWatcher tell your resolver from everyone's."
- Config health: `NoUpstreamResolver` (above) and the DoH case.

### 5.4 Tray, webhooks, MQTT

- **Tray**: `DnsDown` maps to the red icon, tooltip "Name lookups are failing", toast on entry ("Your internet is up, but name lookups are failing — your resolver 10.1.10.1 isn't answering") and on exit, like `LinkDown`.
- **Webhooks**: `DnsDown` passes the "verdict only" filter. The verdict payload gains `dns` (the blame block). Resolver target transitions are ordinary target transitions and respect "Skip explained transitions".
- **MQTT**: `InternetPayload` returns OFF for `DnsDown`. New `dns` **binary sensor** with Home Assistant discovery (`{base}_dns`, device class `connectivity`), ON for Ok/Degraded, OFF for Down, unavailable when not watched; attributes carry `state`, `blame`, and the resolver lists. Verdict attributes gain `dnsBlame`. Per-target binary sensors already exist for every target, resolvers included.

### 5.5 Upgrade and install

- The migration seeds the lookup names and `Enabled = true`. On first start the reconcile loop creates the system resolver targets. No other target changes. `NoUpstreamResolver` shows once.
- The Docker image needs no compose change; the container's resolver is what is measured and is labelled "Docker's resolver".
- README: the privacy section lists the DNS lookups and the seeded names; a "Name resolution" subsection under "How the verdict is decided" explains the rule and the blame line.

## 6. Edge cases

- **Router's forwarder dies, upstream fine.** System resolver Down in about a minute; anchors answer; `DnsDown`, blame `YourResolver`. Tray red, HA internet OFF, `dns` OFF.
- **ISP intercepts or blocks port 53.** System and upstream resolvers both Down; `DnsDown`, blame `NoResolver`.
- **Link down.** Everything Down; `LinkDown` with "name lookups are failing too". Never `DnsDown`.
- **Router dead, resolver is the router.** Resolver is `Unreachable` explained by the router; anchors Down; `LinkDown` blamed on the router. One incident.
- **Two system resolvers, one dead.** `Healthy`, `ResolverPartial` note, `dns` state Degraded.
- **Pi-hole answers NXDOMAIN for a seeded name** (blocklist includes it, unusual but possible). The name misses on that resolver; rotation keeps the others passing; `ResolverLying`/`NameUnresolvable` surfaces it; the admin removes the name. Never `DnsDown` on its own, because one name cannot carry the window below the Down threshold.
- **Captive portal.** Every seeded name answers with the portal address: misses on all, `DnsDown` with `ResolverLying` explaining it.
- **Lookup list emptied and no hostname targets.** Resolvers idle, `DnsNotWatched`, `dns.watched = false`, binary sensor unavailable.
- **DHCP renew changes the resolver.** The network-change event triggers a reconcile: old target retired (disabled, history kept), new one created and burst.
- **Windows DoH.** Probe still runs over port 53; `DnsOverHttps` note explains the gap.
- **Warm start.** Resolver targets are seeded like any other; a seeded Down resolver re-proves on its own evidence.

## 7. Testing

- **ConInfrastructure**: DNS message builder and parser against fixed byte arrays (NOERROR with A records, NOERROR empty, NXDOMAIN, SERVFAIL, REFUSED, truncated, ID mismatch, garbage); expected-answer matching for seeded names; `SystemResolverSource` against a fake `resolv.conf` and a fake interface list, including the Docker address flag.
- **ConEngine** (fake probes, fake time, no sleeps): every §6 case that touches the verdict; reconcile on a changed list (create, retire, re-enable, admin-disabled stays disabled); name rotation per resolver; the 30 s wait cap; `Degraded` never changes the verdict; note start/end for each new kind.
- **Persistence**: `AddDnsWatch` migration on an existing database; export/import/clone round-trip (upstream resolvers and the Dns section travel, system-managed ones do not).
- **API/MCP**: the `dns` block shape; locked config routes; `ApiSurfaceLockTests`.
- **Notifiers**: `InternetPayload(DnsDown) == OFF`; `dns` binary discovery and state payloads; webhook verdict-only filter passes `DnsDown`; tray mapping and toast text.
- **UI (vitest)**: banner case; Name resolution section (watched, degraded, down, not watched); DNS config panel; wizard step including "I don't know".

## 8. Rollout

Each step releasable on `edge`:

1. **Plumbing.** `ProbeKind.Dns`, `IDnsProber`, `TargetScope.Resolver`, `SystemManaged`, `PingProfile.Resolver`, `DnsOptions`, migration, `SystemResolverSource`, reconcile loop. Verdict unchanged; resolvers visible only in `targets[]`.
2. **Verdict.** `DnsDown`, blame, the `dns` status block, the new notes, metrics.
3. **UI.** Banner, Name resolution section, DNS config panel, wizard step, config-health warnings, README.
4. **Consumers.** Tray, webhooks, MQTT binary and sensor.
