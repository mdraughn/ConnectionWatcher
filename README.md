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

Design specs and implementation plans live in `docs/superpowers/`.
