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

Frontend dev loop: `npm run watch` in `Programs/ConWatch/ClientApp` beside
`dotnet run`; `npm test` runs the Vitest suite. `dotnet publish` builds the
bundles automatically.

## Running unattended

**Windows service** — publish, then run the installer elevated:

```powershell
dotnet publish Programs/ConWatch -c Release -o C:\ConWatch
dotnet publish Programs/ConTray -c Release -o C:\ConWatch
powershell -ExecutionPolicy Bypass -File deploy/windows/install-service.ps1 -AppDir C:\ConWatch
```

The installer picks a free port once (default 8080; `-Port` to choose), pins
it, opens the firewall, sets restart-on-failure, creates a Start Menu
shortcut, starts the verdict-colored tray companion (green/orange/red at a
glance; click for the dashboard), and opens the dashboard.
`uninstall-service.ps1` removes everything except your data.

**Docker** — `deploy/docker/compose.yaml` (data in `./data`, port 8080,
`NET_RAW` for ping/traceroute when running non-root). The Dockerfile is
authored but not yet built or run anywhere — local Docker Desktop was unable
to start its engine at authoring time — and Synology deployment is likewise
unverified. Treat the first `docker build` as part of the test.

Design specs and implementation plans live in `docs/superpowers/`.
