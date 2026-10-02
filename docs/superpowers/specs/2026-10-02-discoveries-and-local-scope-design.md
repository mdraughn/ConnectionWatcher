# Discoveries and Local scope — design

Date: 2026-10-02. Status: approved in conversation, awaiting written-spec review.

## Intent

Mark wants ConnectionWatcher to run in a container on a NAS and still know the
facts about the local network that a container may not see (the real router,
the real first hop past the LAN), and to track local devices (bridges, access
points, file servers, cameras, IoT) without letting them affect the internet
verdict.

Earlier ideas considered and dropped: pushing configuration from a PC to a
container (the server's own web page is the place to edit config), and an
"information-only target type" (replaced by a separate Discoveries inbox).
Clone and take-over remain the way to move a setup between machines on the
same LAN.

Success looks like:

- A user can press a button on the server's Config page, or in the Windows
  tray, to search for the router, the network edge, or local devices, and get
  a list of candidates.
- Each candidate can be adopted into a real target or dismissed.
- Adopted local devices are probed with history, shown in a "How's my LAN"
  dashboard section, and never change the verdict or fire notifications.
- Nothing searches automatically.

## Non-goals

- Automatic or scheduled discovery.
- mDNS/SSDP name discovery, port scans, device fingerprinting beyond MAC
  vendor lookup (the Kind/Method fields leave room to add them).
- Parenting local devices to the router so outages are explained as
  "router down" (possible later; v1 local targets are roots).
- Config push/pull between instances.

## 1. The Discovery record

A discovery is a candidate that is not yet a target. Stored in a new
`Discoveries` table (node-local; **never** exported, cloned or part of the
snapshot — dismissals stay with the node that made them).

| Field | Meaning |
|---|---|
| `Id` | Guid |
| `Address`, `Hostname?` | What was found; hostname from reverse DNS when available |
| `Kind` | What it is: `Gateway`, `NetworkEdge`, `LocalDevice` (extensible) |
| `Method` | How it was found: `DefaultRoute`, `Traceroute`, `ArpTable`, `PingSweep` (extensible) |
| `Origin` | Who found it: `server` or `tray:<machine name>` |
| `Evidence` | Short text/JSON: MAC + vendor, hop number, RTT, etc. |
| `FirstSeenUtc`, `LastSeenUtc` | UTC with offset preserved per the project's timestamp rule |
| `Status` | `New`, `Adopted`, `Dismissed` |
| `AdoptedTargetId?` | Set when adopted |

Rules:

- Upsert on `(Address, Kind)`. A rescan updates `LastSeenUtc`, `Evidence`,
  `Origin`, `Method`; it never creates a duplicate.
- A `Dismissed` discovery stays dismissed when found again (LastSeen updates,
  status does not).
- **Adopt** opens the existing target dialog pre-filled (name, address,
  suggested profile); the user chooses `Scope` (`Verdict` or `Local`) and
  saves. Creating the target and marking the discovery `Adopted` happen in one
  transaction (target-create takes an optional `discoveryId`).
- **Managing dismissals.** The dismissal record *is* the `Dismissed` row: it
  persists indefinitely (rows are tiny; no pruning) so a rediscovered address
  stays dismissed. The Discoveries page has a status filter (New — the
  default, Dismissed, Adopted, All); the Dismissed view is the dismissal list.
  Each dismissed row has **Restore** (status back to `New`), and the page has
  **Restore all dismissed**. There is deliberately no separate "forget"
  action: deleting a row would only make it reappear as New on the next scan,
  which is what Restore does directly.
- If an adopted target is later deleted, the discovery stays `Adopted` with a
  dangling id shown as "(target removed)"; the user may dismiss or re-adopt.
  (Decision: no auto-reset, to avoid surprising resurfacing.)

## 2. How searches run

All searching is manual. One scanner library in
`Libraries/ConInfrastructure/Discovery` extends the existing
`GatewayDiscovery`, behind small interfaces (ARP/neighbor-table reader,
pinger, traceroute runner, reverse-DNS, OUI vendor lookup) so it can be tested
with fakes. It is used in two places.

**Server (Config → Discoveries page).** Table of discoveries with Adopt and
Dismiss; search buttons *Find my router* and *Scan local network*, running on
the server and therefore from the server's vantage. When the server appears to
be in a container on a bridge network, results show a warning that a scan there
sees only the container's subnet.

**Tray (Windows).** Menu items *Find my router*, *Find the network edge*,
*Scan this network* run the same scanner on the PC and POST the results to the
server. This is the path for when the container cannot see the LAN. The tray
needs the admin password: prompted on first use and stored per user with DPAPI
(`ProtectedData`, CurrentUser). Transport goes through the existing
`TransportPolicy` (plain HTTP to a non-loopback host with a password is refused
unless explicitly allowed).

Search methods (first cut):

- **Router:** default route plus the existing common-address ICMP probes.
- **Network edge:** traceroute to a public address; the first hop that is not
  in a private range. Reuses the existing traceroute infrastructure (needs
  NET_RAW, already granted in `deploy/docker/compose.yaml`).
- **LAN scan:** ARP/neighbor table plus a ping sweep of the machine's own
  subnet. Private (RFC 1918) ranges only, capped at a /24, bounded concurrency.

Limits: one search at a time per node (second request → 409), overall time cap
(30 s) with cancellation, results returned and upserted when the search ends.

## 3. Local scope and the LAN dashboard

**Field.** `Targets` gain `Scope` ∈ {`Verdict` (default), `Local`} on `TargetRow`,
`TargetDto` and `TargetConfig`, with an EF migration. Older snapshots and
clones import as `Verdict`. Scope is configuration and travels in snapshots.

**Rules** (validated in `ConfigService`): a Local target has no parent and no
children, cannot be an anchor, and cannot be a pool member.

**Monitoring.** The same engine monitors Local targets, so they get state and
recorded transitions. A new built-in **Local** profile provides LAN-appropriate
timing and tolerance for sleepy IoT devices, seeded for existing databases the
same way other built-in profiles are (see the import-restores-built-ins
behavior).

**Exclusions.** Local targets are excluded from: the verdict, suspicion and
attribution, the ISP report (`IspReportBuilder`), and webhook/MQTT
notifications. Prometheus metrics include them with a `scope` label. (To be
verified in implementation: the verdict walks anchor ancestors only, so a root
non-anchor target should not influence it; tests below pin this.)

**Dashboard.** A "How's my LAN" section under the verdict: summary line
("9 of 10 up") and a row per device with state and time since last change.
Verdict colors and the tray icon remain verdict-only.

**History.** A scope filter, defaulting to Internet, so LAN transitions do not
clutter ISP evidence; one click shows them.

## 4. API, clone, MCP, safety

**Endpoints** (all admin-gated; none open):

- `GET /api/v1/discoveries?status=New|Dismissed|Adopted|All` (default `New`)
- `POST /api/v1/discoveries` — batch upsert (tray)
- `POST /api/v1/discoveries/search/router`, `/search/lan` — server-side search
- `POST /api/v1/discoveries/{id}/dismiss`
- `POST /api/v1/discoveries/{id}/restore`
- `POST /api/v1/discoveries/restore-dismissed`
- Target creation accepts optional `discoveryId` (adopt).

**Clone.** Local targets ride in the snapshot like any other target: clone is a
same-LAN operation, and router/path config is equally site-specific. The clone
dialog gains two options: *Include local devices* (default on) and *Include
secrets* (default off; needs the source's admin password, as today). The local
devices option filters on import; it does not change what the source serves.

**Open-export exposure.** `GET /api/v1/export/snapshot` is deliberately open
(no admin, regardless of whether an admin password is set), so adopted Local
targets' names and addresses become readable by anyone who can reach the port,
just as the router address and other targets already are. Accepted for a
homelab; documented in SECURITY.md and the README.

**MCP.** Stays read-only. Adopted Local targets appear through the existing
status/history tools with a `scope` field; a new `list_discoveries` tool returns
the inbox (kind, method, origin, status), New by default with an optional
`status` filter. Searching, adopting and dismissing
are not exposed on MCP (they mutate state or generate network traffic and
remain admin-authenticated REST).

**Container networking (small, adjacent).** Document `network_mode: host` for
Linux/NAS deployments in `deploy/docker/compose.yaml` and the README, with a
note that bridge networking makes hop 1 of a traceroute the Docker bridge and
that host mode does not work on Docker Desktop for Windows.

## 5. Errors

Unreachable server, wrong admin password, search already running, and a
search time-out each produce a distinct, plain message in both the web page
and the tray. Partial scan results are kept when a search is cancelled or
times out.

## 6. Testing

- Scanner with fake ARP/pinger/traceroute/DNS: router, edge and LAN methods;
  private-range and /24 cap enforcement.
- Discovery store: upsert/dedupe, dismissed-stays-dismissed, restore and
  restore-all, status filtering, adopt transaction, dangling adopted target.
- Scope: a Local target going down does not change the verdict, produce a
  notification, or appear in the ISP report; validation rules; migration;
  import of an older snapshot defaults to `Verdict`; built-in Local profile
  seeded and restored on import; clone with and without the local-devices
  option.
- API: admin gating on every discoveries endpoint, 409 on concurrent search,
  batch upsert from the tray.
- MCP: `list_discoveries` and the `scope` field; no mutating tools.
- Anything that waits does so on conditions, never sleep-then-assert (CI
  runners have 2 cores; see the timing-flakes notes).

## 7. Suggested build order

1. `Scope` on targets, Local profile, exclusions, LAN dashboard section,
   history filter, clone option.
2. Discoveries table, endpoints, Config → Discoveries page, router and edge
   search, adopt flow, MCP tool.
3. LAN scan (ARP + sweep) on the server.
4. Tray scouting and password storage.
5. Compose/README docs for host networking.

Each step is independently shippable.
