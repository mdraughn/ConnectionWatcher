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
