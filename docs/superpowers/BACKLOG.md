# ConnectionWatcher Backlog

Ideas noted for later; each gets the usual spec → plan → build cycle when picked up.

## Measurement-data handling (noted 2026-08-21)

- ~~**Export data**~~ — DONE 2026-08-22 (`/api/v1/export/*`, CSV/JSON, Admin page).
- ~~**Start over**~~ — DONE 2026-08-22 (Admin danger zone: CLEAR / factory RESET).
- **Prune old data** — user-initiated deletion beyond the automatic
  downsampling: e.g. "drop everything before {date}" or "drop this target's
  history". Should respect the history-semantics model (a prune is not a
  HistoryReset; it just removes rows). VACUUM after big deletes.

## Integrations (noted 2026-08-21)

- **Prometheus + Grafana** — expose a `/metrics` endpoint (OpenMetrics): per-
  target state, success rate, RTT gauges, plus the verdict as a labeled gauge.
  Grafana (containerized on the NAS) dashboards it alongside the rest of the
  homelab. Makes ConnectionWatcher a citizen of a broader monitoring stack.
- ~~**Healthchecks heartbeat (watch the watchman)**~~ — DONE 2026-08-31:
  `HeartbeatNotifier` GETs `Heartbeat.Url` every `Heartbeat.IntervalMinutes`
  (default 5) while set; immediate ping on (re)configure; Admin Notifications
  section. The receiving service alerts when pings stop.
- ~~**ISP evidence report**~~ — DONE 2026-08-22 (`/Report`, printable,
  verdict-attributed; SiteIncident excluded by design).
- **MCP server** — expose query tools (`current_status`, `outages_since`,
  `weather_history`, maybe `run_traceroute`) so Claude can answer "how was my
  internet last night?" from the actual database, or triage a slow-feeling
  connection on request.

## Distribution (planned 2026-08-31; not a money-making project — MIT licensed)

Three public artifacts cover essentially everyone:

- ~~**Windows installer**~~ — DONE 2026-08-31: `Build-Installer.ps1` →
  Inno Setup `ConnectionWatcher-Setup-<ver>.exe`, self-contained win-x64,
  component choice (service+tray / tray-only with server-URL page), familiar
  install/uninstall, data in ProgramData always preserved.
- **Multi-arch container publish** — one image tag for linux/amd64 +
  linux/arm64 via `docker buildx`, pushed to a registry (GHCR when the repo
  goes to GitHub). Covers Intel/ARM Synology, Pi, any Linux, Docker on Mac.
- **Release pipeline** — GitHub Actions on version tag: build installer,
  build+push image, attach to a GitHub Release. Version scheme shared between
  installer and image tag.
- **Code signing** (when going public) — unsigned installers trip
  SmartScreen; cert costs ~$100-400/yr. The single biggest polish item for
  strangers; irrelevant while it's just Mark.
- Later if asked: win-arm64, .deb + systemd unit, Homebrew, Synology SPK,
  Home Assistant add-on, in-app "update available" check against GitHub
  releases.

## Fast detection & glitches — DONE 2026-09-06

Suspicion bursts on first loss, corroboration sweeps across anchors+trunk,
1s burst timeout, glitch ledger (sub-Faltering episodes, 2+ losses, 60s
coalescing), Internet-glitch correlation, recurrence heatmap on History,
ISP-report glitch section with top-hours clustering and chronic badges.
Explicit non-goal (Mark): latency/RTT-based glitches.

## Time reporting (noted 2026-09-06)

- **ISP report / History timezone clarity** — the report prints UTC with the
  label only in the header; History renders browser-local. Cross-referencing
  the two confused even Mark (a 10:34 PM Sep 3 local event reads as
  "03:34 Sep 4" on the report). Label each incident row's zone explicitly,
  or render a configurable zone with UTC alongside.

## In design (noted 2026-09-10)

- ~~**Incident display redesign**~~ — DONE 2026-09-11: punchcard + ledger on
  the dashboard, cause-aligned ribbon board on History, shared encoding via
  reusable mark primitives (`marks.tsx`, `encoding.ts`) and a page-agnostic
  story lib. Twin WAN lanes still pending failover groups. Spec:
  `specs/2026-09-10-incident-display-redesign-design.md`.
- **Dual-WAN failover groups** — twin External Router lanes; active-path
  detection by RTT asymmetry (ping both ISPs' public edge routers; the
  standby answers "the long way around"); trunk becomes an or-parent group;
  LinkDown = both lanes down. Waiting on the second ISP's install.
- ~~**ISP naming**~~ — DONE 2026-09-11: `Operator` tag end to end (schema,
  CRUD/import/snapshot, Targets form, wizard question, Link-verdict wording,
  report title, MQTT operator attribute). Migrates onto lane entities when
  failover groups land. Notes:
  `specs/2026-09-10-isp-naming-and-clone-install-notes.md`.
- **Clone-install + master enable (switch-over)** — wizard option "copy from
  a running node" via the snapshot API/importer; `Engine.Enabled` switch +
  admin API; take-over choreography (disable old → final sync → enable new)
  gives near-zero-gap migration. Same notes file.

## Small hardening candidates

- **Config health warning: pool parent vs. members' parent drift** (noted
  2026-09-11). Re-parenting a pool's members doesn't move the pool's own
  parent; the verdict layer is immune (walks anchor chains) but the ISP
  report's trunk walk starts at pool.ParentId and silently truncates — bit
  us on the NAS after the Comcast Edge re-parent. Add a ConfigHealth warning
  when the pool's parent is not on its members' shared parent chain, or make
  UpsertTarget re-parenting drag the pool along.

## Still on the roadmap

- ~~**Container build smoke test**~~ — DONE 2026-08-24: image builds, boots
  healthy, and real ICMP works in-container (8.8.8.8 → Responding). Remaining:
  Synology verification when Mark has setup time.
- **Phase 4** — deployment + notifications:
  - Docker packaging for Synology (traceroute needs NET_RAW; `data/` volume).
  - **Windows Service** for always-on operation on a Windows box
    (AddWindowsService + install script, restart-on-failure) — added 2026-08-22.
    Install script probes for a free port ONCE (default 8080, then walk
    candidates or -Port override), persists it, creates the matching firewall
    rule, and prints the final URL; runtime bind failure logs a clear
    port-in-use message instead of crash-looping mutely.
    Discoverability: installer creates a Start Menu shortcut to the dashboard
    and opens it once at install end. Plus a **tray companion** (separate
    user-session app — services can't own tray icons): verdict-colored icon
    (green/orange/red, gray = service down), left-click opens dashboard,
    right-click menu (Dashboard/History/Report/Admin, start service, exit),
    auto-start at login. Optional later: toast notifications on verdict
    changes (decide alongside MQTT/webhooks, not by accident).
  - ~~**Webhooks + MQTT toward Home Assistant**~~ — DONE 2026-08-29 (phase 4b):
    ConNotify library; HA MQTT discovery (verdict sensor, internet up/down
    binary, per-target connectivity binaries, LWT availability), transitions
    JSON topic, generic webhooks with verdict-only filter; Admin Notifications
    section with broker Test button; verified end-to-end against Mosquitto in
    Docker. Remaining user-side: point it at the real HA broker.
