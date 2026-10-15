# ConnectionWatcher

[![ci](https://github.com/mdraughn/ConnectionWatcher/actions/workflows/ci.yml/badge.svg)](https://github.com/mdraughn/ConnectionWatcher/actions/workflows/ci.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

A home internet connectivity monitor. It pings a hierarchy of targets — LAN
devices, the router, the cable modem, upstream ISP hops, and external anchors
like Google and Cloudflare — and answers the question *"is my internet down,
and what kind of down is it?"*: a single site's incident, general internet
weather, or your own link.

![The dashboard: verdict, uptime clock, and the path to the internet](docs/images/dashboard.png)

When your connection *is* the problem, ConnectionWatcher tells you which hop
broke, keeps the evidence (a printable ISP report with timestamps your
provider can correlate), and can page you through Home Assistant, MQTT, or
webhooks. When it isn't, it says so — a Netflix outage should never send you
to reboot your modem.

## Quick start (Docker)

```bash
docker run -d --name conwatch --restart unless-stopped \
  -p 8080:8080 -v ./data:/app/data --cap-add NET_RAW \
  ghcr.io/mdraughn/conwatch:latest
```

Open `http://localhost:8080` — the quick-setup wizard detects your router,
offers four public DNS anchors, and starts monitoring in one click. The image
is multi-arch (amd64 + arm64: Intel/ARM Synology, Raspberry Pi, any Linux);
`deploy/docker/compose.yaml` has the same thing in compose form.

Image tags: `latest` is the newest stable release (also `1.2.3`, `1.2`, `1`
to pin a line); `edge` is the newest build from `master`, for people who
like to live there. Every build also carries a `sha-<commit>` tag. Windows
installers ship with each release on the
[Releases page](https://github.com/mdraughn/ConnectionWatcher/releases): a
Setup wizard and a zip. Either way the result is one folder with the
`conwatch` command — see [Running unattended](#running-unattended).

## Running from source

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
  Timestamps in query strings (`from`, `to`) are ISO-8601; an explicit offset
  is honored and **no offset means UTC**, never the server's local zone.
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

Restarts are warm: after an update, reboot, or config change, ConWatch resumes
its last known states immediately (shown as *assumed* and burst-confirmed by
fresh pings within seconds) instead of re-measuring the world from scratch —
the timelines stay continuous and no restart noise is recorded. Seeding from
the database applies only when the watcher was down briefly (the
`Engine.WarmRestartMaxGapMinutes` setting, default 10 minutes; 0 disables).

Hostname targets follow DNS: the name is re-resolved every five minutes and
immediately when the target goes Down, and an address that has left the
answer set is replaced without resetting the target's history. Two targets
may share an address (say, the router by ICMP and by `tcp:443`); each is
probed and tracked on its own.

## Web UI

ConWatch serves its UI at the root:

- **Dashboard** (`/`) — verdict banner, live reachability tree (SignalR)
  with per-target success rate and latency at a glance. Bottom status bar
  shows the latest event.
- **Dashboard story** — around the live tree: the **event strip** (the last
  N minutes as one row of minute bins right under the verdict banner), the
  **incident ledger** (the 10 most recent events, each as one plain-English
  sentence — "Your link went down for 4 min. LinkDown: Comcast — Comcast
  Edge is Down"), and the recent-glitch list. All displays share one
  encoding: shape = scope (bar = shared path, dot = one site), hue = whose
  gear (warm = out there, grey = yours), depth = severity (glitch →
  degraded → out).
- **History** (`/History`) — the evidence: uptime stats, latency band, loss
  chart, per-subject state ribbon, the **recurrence punchcard** (hours ×
  days over 30 days; a nightly gremlin reads as a horizontal run), the
  **cause-aligned ribbon board** — every target on one shared axis, trunk
  rows in grey, hatching for "unreachable behind a down parent", so one
  incident reads as a vertical stripe across exactly the rows it touched —
  and a 90-day incident ledger. Detection is aggressive: the first lost
  ping starts a 1-second-interval burst on that target AND a corroboration
  sweep across all anchors and the trunk, so even a few-second wobble yields
  enough evidence to classify.
- **Data** (`/Data`) — the record: database statistics, all exports
  (transitions/rollups/glitches as CSV or JSON, the sanitized snapshot
  document), the ISP report link, and the retention settings that keep the
  database from growing forever (minute rollups → hourly → daily).
- **Targets** (`/Config/Targets`, `/Config/Profiles`, `/Config/Pools`) — CRUD
  with inline validation errors, plus whole-config export/import. Targets can
  carry an optional **ISP/operator name** ("Comcast"): link verdicts then say
  "LinkDown: Comcast — External Router is Down", the ISP report titles itself
  with the provider, and the MQTT verdict attributes carry it. The setup
  wizard asks once ("Who is your internet provider?") and tags the WAN hop,
  and tags its anchors with their providers. The verdict counts degraded
  **operators**, not targets: four addresses of one provider failing together
  are one piece of evidence and stay a site incident, never internet weather.
  Untagged anchors each count as their own operator.
- **Traceroute** (`/Traceroute`) — discover upstream hops and add them as
  monitored targets with the right parent pre-filled.

Each target probes over **ICMP ping** by default, or a **TCP connect** to a
chosen port for devices that firewall ping but still run a service — a
refused connection still counts as a reply, since an RST proves the host's
stack answered. Private addresses (RFC1918, loopback, link-local) allow any
port; public addresses are restricted to 80, 443, 53, where a bare
connect-and-close looks like ordinary client traffic. The Targets form's
**Test** button tries ICMP and the policy-allowed TCP ports for the typed
address in one click and preselects whichever answered; for a hostname it
also lists every address in the DNS answer set, so you can see what is being
pinned.

## Notifications (Home Assistant MQTT + webhooks)

Configured on the **Admin** page under *Notifications*; both are off by
default and reconfigure live — no restart needed.

**MQTT / Home Assistant** — enter your broker (host, port, optional
credentials), press *Test connection*, and enable. ConWatch publishes Home
Assistant discovery documents, so entities appear automatically under one
"ConnectionWatcher" device:

- a **verdict sensor** (`Healthy` / `SiteIncident` / `InternetWeather` /
  `LinkDegraded` / `DnsDown` / `LinkDown`, with the detail text as an attribute),
- an **internet up/down binary sensor** (OFF only when the verdict is
  LinkDown or DnsDown — the simplest summary),
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
fields). `timestamp` is ISO-8601 with an explicit `+00:00` offset. Set
*Verdict changes only* to be notified just when your internet weather changes
rather than on every target flap.

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

## Moving to a new machine (clone + take over)

The setup wizard on a fresh install offers **"Copy everything from a running
ConnectionWatcher"**: enter the old node's URL (and its admin password, if
set) and the new node arrives with the full configuration, history, and —
over the authenticated channel — the notification secrets. It starts as a
**disabled read-only archive** while the old node keeps monitoring; verify it
looks right, then press **Take over** on its Admin page: the old node is
disabled, one final snapshot closes the data gap to seconds, and the new node
goes live (warm-started, so the timeline stays continuous). The old node
remains a browsable archive until you decommission it.

Transport honesty: cloning over plain HTTP to a non-local address sends the
password and receives secrets unencrypted — the wizard stops and offers the
HTTPS address, an explicit "send it anyway", or a secret-less clone. The
master switch behind all this is the **Monitoring** toggle on the Admin page
(`Engine.Enabled`): off = archive mode — history, reports, and exports keep
serving; pinging and notifications stop, and MQTT reads offline in Home
Assistant.

## Privacy

ConnectionWatcher has **no telemetry**: nothing phones home to the author or
anyone else — no usage analytics, no crash reporting, no version checks (if
an update check is ever added, it will be opt-in and documented here). The
complete list of network traffic the app can generate:

- ICMP pings, TCP connects and DNS lookups for the targets **you** configured
  (that's the product), plus the name-resolution watch's DNS queries: it asks
  the resolvers your machine is configured with for a short list of lookup
  names (seeded with `dns.google`, `one.one.one.one`, `dns.quad9.net` and
  `dns.opendns.com`, editable in the `Dns.LookupNames` setting) and for the
  names of your hostname targets; turn the watch off (`Dns.Enabled`) and those
  queries stop;
- only if you enable them, and only to endpoints you typed in: MQTT to your
  broker, webhooks to your URLs, the heartbeat ping;
- only when you trigger them: the setup wizard's router probe (your own
  gateway), traceroute, and clone/take-over calls to a node you named.

The web UI is self-contained — CSS, fonts, and scripts are served by the app,
so browsers make no third-party requests. Logging goes to stdout only (your
container's log, or Warning+ to the Windows Event Log as a service); nothing
is written to log files, credentials are never logged, and webhook/heartbeat
URLs appear in logs with their secret paths elided (`https://hc-ping.com/…`)
so a pasted log can't leak them.

## How the verdict is decided

The internet is up when at least one anchor outside your ISP answers; the path
(router, modem, ISP hops) only says *where* the problem is when the anchors say
it is down.

### Name resolution

The resolvers your machine uses are asked for a few well-known names. When the
anchors answer but every one of those resolvers is down, the verdict is "UP, but
name lookups are failing", and the banner says whether it is your resolver or
every resolver. A resolver that blocks the seeded names (a Pi-hole, say) is
shown as filtered, not down. The Home Assistant internet binary sensor now reads
OFF during a name-resolution failure as well as during a link outage.

## Ask Claude about your internet (MCP)

ConnectionWatcher exposes a Model Context Protocol endpoint at `/mcp`, so an
MCP host can query the node directly:

```bash
claude mcp add --transport http conwatch http://nas.example.com:8080/mcp
```

Then just ask — "how was my internet last night?", "any glitches today?",
"what did the verdict blame on Tuesday?" — and Claude answers from the actual
database via four read-only tools: `get_current_status`,
`get_connection_report`, `get_transitions`, and `get_glitches`. The endpoint
is open like the rest of the watching surface and serves nothing the open
APIs don't already serve; it never touches settings, secrets, or admin
actions.

## Grafana / Prometheus (metrics)

ConnectionWatcher exposes Prometheus metrics at `/metrics` — open like the
rest of the watching surface, and serving nothing `/api/v1/status` doesn't.
Scrape config:

```yaml
scrape_configs:
  - job_name: conwatch
    scrape_interval: 15s
    static_configs:
      - targets: ["nas.example.com:8080"]
```

What you get: `conwatch_verdict{state=...}` (one-hot — feed it straight to a
Grafana state-timeline panel), per-target `conwatch_target_up`,
`conwatch_target_loss_ratio` (packet loss, 0..1) and
`conwatch_target_rtt_seconds`, `conwatch_pool_up`, plus
`conwatch_transitions_total` and `conwatch_glitches_total` counters for
`rate()` panels. Useful starters:

```promql
conwatch_target_rtt_seconds * 1000            # per-target latency in ms
avg(conwatch_target_loss_ratio) * 100         # overall packet loss %
increase(conwatch_transitions_total[1h])      # instability by target
```

## Running unattended

**Wizard** — run `ConnectionWatcher-Setup-<version>.exe`. It installs to
`C:\Program Files\DSoftCo\ConnectionWatcher`, asks for the dashboard port,
registers the service, opens the firewall, starts the tray companion at
sign-in, and opens the dashboard. The first page asks whether the service
runs on this computer or somewhere else: uncheck it on a machine that only
needs the tray, and the next page asks for the server's address (keep the
default if you don't know it yet; the tray's Settings dialog can change it
later). Uninstalling removes the program, service and tray, then asks
before touching your data. Release builds are code-signed (publisher "Mark
Draughn"); SmartScreen may still ask for confirmation until the certificate
has built up reputation.

**Zip** — unzip `ConnectionWatcher-<version>-win-x64.zip` anywhere and, from
a terminal in that folder:

```powershell
conwatch service install --port 8080
conwatch tray install
conwatch service status
```

The first command asks for administrator rights once. Both installs are the
same folder and the same commands; the wizard just clicks them for you.

**The `conwatch` command** does everything else:

| Command | What it does |
|---|---|
| `conwatch serve` | Run the watcher in the foreground (this is also what the service runs). |
| `conwatch service install\|uninstall\|start\|stop` | Manage the Windows service (elevates once). |
| `conwatch service status` | Installed? Running? Which port and data folder? Does the dashboard answer? |
| `conwatch config show` | Every setting and where it came from. |
| `conwatch config set listen-url\|data-dir\|server-url <value>` | Change one; listen-url/data-dir take effect on the next service start. To change the port use service install --port. |
| `conwatch tray` / `conwatch tray install\|uninstall` | Start the tray now / at sign-in. |

Settings live in `%ProgramData%\DSoftCo\ConnectionWatcher\conwatch.json`
(listen URL, data folder, the tray's server URL) with a per-user override in
`%AppData%\DSoftCo\ConnectionWatcher\tray.json` written by the tray's
Settings dialog or `conwatch config set server-url <url> --user`. Data stays
in `%ProgramData%\DSoftCo\ConnectionWatcher\data`. Uninstalling never deletes
either without asking. To move the dashboard to another port, run
`conwatch service install --port <n>` again; it re-pins the port, firewall
rule and shortcut and restarts the service.

**Tray companion** — `conwatch-tray.exe`: green/orange/red/gray at a glance;
double-click opens the dashboard; a single click (either button) opens the
menu: History, the ISP report, Admin, and **Settings** (server URL with
test-before-save, back to the installed default, toast notifications). It runs on machines that are not
running the server too: point it at the NAS with `conwatch tray install --url http://nas:8080`.

**Upgrading from 0.9** — the wizard replaces `ConWatch.exe` and `ConTray.exe`
with `conwatch.exe` and `conwatch-tray.exe`, re-registers the service, moves
your tray URL from `contray.json` into the new config file, and removes the
old `Watcher\` and `Tray\` folders. The `Install-*.ps1` scripts and
`deploy/windows/*.ps1` are gone. The tray reads the machine config when it
starts, so after `conwatch config set` restart the tray to pick up a new
installed default.

**Reverse proxy / HTTPS** — ConWatch is proxy-friendly by construction: it
emits only relative URLs and never reads scheme, host, or client IP, so an
HTTPS-terminating proxy (e.g. Synology DSM's reverse proxy with the existing
cert) needs no app-side configuration. Two rules: proxy it at the **root of
its own hostname** (sub-path mounting is unsupported), and enable the
proxy's **WebSocket** headers or the dashboard's live updates stall. If a
future feature consumes client IPs or the original scheme, add
`UseForwardedHeaders` then — noting that inside Docker the proxy's requests
arrive from the bridge gateway, not loopback, so the trusted-proxy config
must name the bridge network.

**Docker** — `deploy/docker/compose.yaml` (data in `./data`, port 8080,
`NET_RAW` for ping/traceroute when running non-root). This is the reference
deployment: the multi-arch image is published to
`ghcr.io/mdraughn/conwatch` by CI on every merge and runs in production on
Synology Container Manager. `Build-Images.ps1` builds the same image
locally (amd64 + arm64 via cross-compilation) when you want to. The compose
file sets `stop_grace_period: 30s` so the last minute of rollups is written
on stop; if you run the image with `docker run`, add `--stop-timeout 30`.

## Development

| Project | Purpose |
|---|---|
| `Programs/ConWatch` | The web host: monitoring engine + SQLite persistence + REST API + UI (what the container runs) |
| `Programs/ConWatchCli` | `conwatch.exe`: hosts the same app on Windows and manages the service, config and tray |
| `Programs/ConWatchTray` | `conwatch-tray.exe`, the Windows tray companion |
| `Core/ConEngine` | The engine: adaptive ping disciplines, reachability tree, pools, verdict layer |
| `Core/ConCore` | Contracts and domain primitives |
| `Libraries/ConConfig` | The install-time config files (`conwatch.json`, `tray.json`) and their precedence |
| `Libraries/ConInfrastructure` | Real ICMP/TCP probes, DNS, gateway discovery, traceroute |
| `Libraries/ConPersistence` | EF Core (SQLite) schema, config store, transition/rollup recording, retention |
| `Libraries/ConNotify` | MQTT (Home Assistant), webhook, and heartbeat notifiers |
| `Tests/*` | xUnit suites for the engine, persistence, config, the command, and the full app over HTTP |

See [CONTRIBUTING.md](CONTRIBUTING.md) for the dev loop and test commands.
Design specs and implementation plans live in `docs/superpowers/` — the
project's full engineering history, often the fastest answer to "why is
this the way it is."
