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

- ~~**Prometheus + Grafana**~~ — DONE 2026-09-14: open `/metrics` (hand-rolled
  text exposition, no exporter dependency): one-hot verdict gauge, per-target
  up/loss/RTT/sample gauges, pool up, transitions + glitches counters via
  `MetricsCounters` riding the event streams; README scrape config + starter
  PromQL. Grafana dashboard itself lives in Mark's other session.
- ~~**Healthchecks heartbeat (watch the watchman)**~~ — DONE 2026-08-31:
  `HeartbeatNotifier` GETs `Heartbeat.Url` every `Heartbeat.IntervalMinutes`
  (default 5) while set; immediate ping on (re)configure; Admin Notifications
  section. The receiving service alerts when pings stop.
- ~~**ISP evidence report**~~ — DONE 2026-08-22 (`/Report`, printable,
  verdict-attributed; SiteIncident excluded by design).
- ~~**MCP server**~~ — DONE 2026-09-13: `/mcp` (stateless streamable HTTP),
  four read-only tools (`get_current_status`, `get_connection_report`,
  `get_transitions`, `get_glitches`); open like the watching surface.

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

- ~~**ISP report / History timezone clarity**~~ — DONE 2026-09-13: report
  timestamps render browser-local (zone named at the top) with the fixed UTC
  value beneath each for provider-log correlation; no-JS shows UTC only.
  History labels its zone next to the range picker.

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
- ~~**Clone-install + master enable (switch-over)**~~ — DONE 2026-09-12:
  wizard "copy from a running node" (full snapshot over the authenticated
  single-shot channel; requester-side transport policy with insecure-HTTP
  override or secret-less fallback), `Engine.Enabled` archive mode (MQTT
  offline, heartbeat paused, health "Disabled", read-only UI), take-over
  choreography with final sync + warm-started hand-off, both-active health
  warning. Spec: `specs/2026-09-12-clone-takeover-design.md`.

## Warm restart follow-ons (noted 2026-09-11)

- ~~**Warm restart (options 2+1)**~~ — DONE 2026-09-11: seeded presumed states
  (DB on process start, live-engine capture on config rebuilds) + startup
  warm-up burst; `Engine.WarmRestartMaxGapMinutes` setting (default 10, ≤0
  disables); `assumed` flag through status/UI. Spec:
  `specs/2026-09-11-warm-restart-design.md`.
- **Redis-backed blue-green handoff** — Mark's idea, parked as overkill for
  now: shared hot state in Redis would let a NEW container take over mid-flight
  (zero-gap deploys) without violating SQLite's single-writer constraint.
  Only worth it if deploy gaps ever matter again; pairs with the clone-install
  take-over choreography.
- **Burst on parent recovery** — after a Down parent recovers, released
  children re-prove at normal cadence (~100 s). A TriggerBurst on Release
  would settle them in seconds; deliberately left out of warm-restart scope.

## Probe types (noted 2026-09-14)

- **TCP-connect probe** — per-target probe method: ICMP (default) or a TCP
  connect to a given port (`tcp:80`), where "reply = connection accepted".
  Motivating case: Mark's Comcast Business gateway in bridge mode serves its
  admin page at 10.1.10.1 but firewalls ping, so the only ICMP-answering
  address it offers is its hairpinned management WAN IP (jittery ~25 ms
  baseline, drifts with the DHCP lease). A TCP probe would make the modem a
  clean local "is it alive" target — and generally covers any device that
  firewalls ICMP while running services. Touches: engine probe discipline
  (transceiver abstraction currently ICMP-only), target schema/config field,
  Targets form, snapshot/clone round-trip.

## Small hardening candidates

- ~~**Config health warning: pool parent vs. members' parent drift**~~ —
  DONE 2026-09-13: ConfigHealth computes the members' deepest shared
  ancestor and warns (with the exact re-parent to make) when the pool's
  parent is not it, including the unparented-pool case.
- ~~**Transitions retention**~~ — DONE 2026-09-13: `Retention.TransitionMonths`
  (default 24) in the nightly pass; each subject's latest transition always
  survives for warm-restart seeding. Editable on the Data page.

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
