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
- **Healthchecks heartbeat (watch the watchman)** — ping a dead-man's-switch
  (healthchecks.io or self-hosted) every few minutes so a dead NAS/container/
  app produces an alert instead of silence. Small effort, closes a real blind
  spot: today, monitor-down looks identical to all-healthy.
- ~~**ISP evidence report**~~ — DONE 2026-08-22 (`/Report`, printable,
  verdict-attributed; SiteIncident excluded by design).
- **MCP server** — expose query tools (`current_status`, `outages_since`,
  `weather_history`, maybe `run_traceroute`) so Claude can answer "how was my
  internet last night?" from the actual database, or triage a slow-feeling
  connection on request.

## Still on the roadmap

- **Phase 4** — deployment + notifications:
  - Docker packaging for Synology (traceroute needs NET_RAW; `data/` volume).
  - **Windows Service** for always-on operation on a Windows box
    (AddWindowsService + install script, fixed listen URL, firewall rule,
    restart-on-failure) — added 2026-08-22.
  - Webhooks + MQTT toward Home Assistant (MQTT discovery).
