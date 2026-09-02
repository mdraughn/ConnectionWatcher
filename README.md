# ConnectionWatcher

A home internet connectivity monitor. It pings a hierarchy of targets — LAN
devices, the router, the cable modem, upstream ISP hops, and external anchors
like Google and Cloudflare — and answers the question *"is my internet down,
and what kind of down is it?"*: a single site's incident, general internet
weather, or your own link.

## Projects

| Project | Purpose |
|---|---|
| `Programs/ConWatch` | The production app: monitoring engine + SQLite persistence + REST API |
| `Core/ConEngine` | The engine: adaptive ping disciplines, reachability tree, pools, verdict layer |
| `Core/ConCore` | Contracts and domain primitives |
| `ConInfrastructure` | Real ICMP ping + DNS implementations |
| `ConPersistence` | EF Core (SQLite) schema, config store, transition/rollup recording, retention |
| `ConTinker` | Windows-only dev harness with an interactive WPF ping simulator |
| `Tests/*` | xUnit suites for engine, persistence, and the full app over HTTP |

## Running ConWatch

```powershell
dotnet run --project Programs/ConWatch
```

- The SQLite database lives at `data/conwatch.db` (relative to the working
  directory; configurable via `Persistence:DbPath`). Schema migrations apply
  automatically at startup.
- Configuration (targets, profiles, pools) lives **in the database** and is
  edited through the REST API under `/api/v1` — Swagger UI at `/swagger` in
  Development. On first boot, a `Watcher` section in appsettings.json (if
  present) seeds the config; after that the DB is the source of truth.
- The verdict layer (`Verdict:AnchorPool`) names the pool whose members act as
  internet anchors; with intermediates healthy, one degraded anchor is a
  SiteIncident, two or more (configurable) is InternetWeather.

## First run

With an empty configuration, ConWatch opens the **quick-setup wizard**: it
detects your router (confirm it — inside a container the detected gateway may
be the container bridge), offers four public anycast DNS anchors, optionally
discovers your router's WAN address via UPnP, and starts monitoring with one
click. The **Admin** page holds database stats, retention/verdict settings,
and the danger zone (clear history / factory reset, type-to-confirm).

## Web UI

ConWatch serves its UI at the root:

- **Dashboard** (`/`) — verdict banner, live reachability tree (SignalR).
  Misbehaving targets expand in place into tiles with a 60-minute chart and the
  transition evidence; click any row to pin it open. Bottom status bar shows
  the latest event.
- **History** (`/History`) — uptime stats, latency band, loss chart, and a
  state-timeline ribbon per target; the "Internet (verdict)" subject shows your
  internet-weather history.
- **Config** (`/Config/Targets`, `/Config/Profiles`, `/Config/Pools`) — CRUD
  with inline validation errors, plus whole-config export/import.
- **Traceroute** (`/Traceroute`) — discover upstream hops and add them as
  monitored targets with the right parent pre-filled.

## Notifications (Home Assistant MQTT + webhooks)

Configured on the **Admin** page under *Notifications*; both are off by
default and reconfigure live — no restart needed.

**MQTT / Home Assistant** — enter your broker (host, port, optional
credentials), press *Test connection*, and enable. ConWatch publishes Home
Assistant discovery documents, so entities appear automatically under one
"ConnectionWatcher" device:

- a **verdict sensor** (`Healthy` / `SiteIncident` / `InternetWeather` /
  `LinkDown`, with the detail text as an attribute),
- an **internet up/down binary sensor** (OFF only when the verdict is
  LinkDown — the simplest summary),
- a **connectivity binary sensor per target** (ON while Responding, Faltering,
  or Recovering; OFF when Down or Unreachable).

State topics are retained under the base topic (default `conwatch/…`), with a
last-will `availability` topic so entities show *unavailable* if ConWatch
stops. Every transition is also published as JSON to `conwatch/transitions`
for automations that want the raw events. Publishes happen only on state
changes and reconnects — never per ping.

**Heartbeat** — point `Heartbeat.Url` at a dead-man's-switch service
(healthchecks.io or self-hosted) and ConnectionWatcher GETs it every few
minutes to say "still alive"; the service alerts you when the pings stop —
a dead NAS, a crashed container, or a full internet outage. Watches the
watchman.

**Webhooks** — one URL per line; each transition is POSTed as JSON
(`subjectKind`, `subjectName`, `from`, `to`, `trigger`, verdict/evidence
fields). Set *Verdict changes only* to be notified just when your internet
weather changes rather than on every target flap.

**Admin password** (optional) — set one on the Admin page to lock Config,
Admin, Traceroute, and Setup (pages and APIs) behind a single site password.
Watching stays open. A 🔒 button on the dashboard enters admin mode for the
browser session. It stops casual tinkering, not attackers: forgot the
password? Delete `admin-password.txt` next to the database
(`C:\ProgramData\DSoftCo\ConnectionWatcher\data\` on Windows, the mapped
`data/` folder on Docker/Synology) and reload.

Frontend dev loop: `npm run watch` in `Programs/ConWatch/ClientApp` beside
`dotnet run`; `npm test` runs the Vitest suite. `dotnet publish` builds the
bundles automatically.

## Running unattended

**The easy way** — from the repo root, no parameters, self-elevating:

```powershell
.\Install-Watcher.ps1
```

```powershell
.\Install-Tray.ps1
```

`Install-Watcher` publishes to `C:\Program Files\DSoftCo\ConnectionWatcher\Watcher`,
installs the service (data in `C:\ProgramData\DSoftCo\ConnectionWatcher\data`),
and opens the dashboard. `Install-Tray` publishes the dot and points it at the
installed service automatically. `Publish-*` re-deploys after changes
(stopping/restarting the service or tray around the copy), `Build-*` is a
quick compile check, and `Uninstall-*` removes service/autostart while leaving
files and data in place.

**By hand** — publish anywhere and run the deploy scripts with parameters:

```powershell
dotnet publish Programs/ConWatch -c Release -o C:\ConWatch
powershell -ExecutionPolicy Bypass -File deploy/windows/install-service.ps1 -AppDir C:\ConWatch
```

The installer picks a free port once (default 8080; `-Port` to choose), pins
it, opens the firewall, sets restart-on-failure, creates a Start Menu
shortcut, starts the verdict-colored tray companion, and opens the dashboard.
`uninstall-service.ps1` removes everything except your data.

**Tray companion (standalone)** — the dot also installs on machines that
*aren't* running the server (point it at the NAS or another box):

```powershell
dotnet publish Programs/ConTray -c Release -o C:\ConTray
powershell -ExecutionPolicy Bypass -File deploy/windows/install-tray.ps1 -AppDir C:\ConTray -Url http://nas:8080
```

No elevation needed. Green/orange/red/gray at a glance; double-click opens the
dashboard; right-click for History, the ISP report, Admin, and **Settings**
(override the server URL, test the connection, return to the installed
default, toggle toast notifications on state changes).

**Docker** — `deploy/docker/compose.yaml` (data in `./data`, port 8080,
`NET_RAW` for ping/traceroute when running non-root). Built and smoke-tested
locally on Docker Desktop (health endpoint + real in-container ICMP verified);
not yet verified on Synology hardware. `Build-Images.ps1` builds the image
for both `linux/amd64` and `linux/arm64` (Intel and ARM Synology/Pi) via
cross-compilation, and with `-Push` publishes both as a single multi-arch
tag to a registry.

Design specs and implementation plans live in `docs/superpowers/`.
