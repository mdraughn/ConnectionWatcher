# ConnectionWatcher Backlog

Ideas noted for later; each gets the usual spec → plan → build cycle when picked up.

## Measurement-data handling (noted 2026-08-21)

Config export/import exists, but the *measurement* data (transitions, rollups)
has no management story yet:

- **Export data** — dump transitions/rollups (CSV and/or JSON), per target or
  whole database, for analysis outside the app.
- **Prune old data** — user-initiated deletion beyond the automatic
  downsampling: e.g. "drop everything before {date}" or "drop this target's
  history". Should respect the history-semantics model (a prune is not a
  HistoryReset; it just removes rows).
- **Start over** — wipe all measurement data (keep config) and begin fresh;
  useful after experimentation or a topology overhaul. Probably a
  confirm-hard UI ("type the word") given irreversibility.

Notes: the retention service already downsamples automatically; these are
*manual* controls on top. Watch SQLite file-size behavior (VACUUM after big
deletes). API + Config-page placement to be decided at design time.

## Integrations (noted 2026-08-21)

- **Prometheus + Grafana** — expose a `/metrics` endpoint (OpenMetrics): per-
  target state, success rate, RTT gauges, plus the verdict as a labeled gauge.
  Grafana (containerized on the NAS) dashboards it alongside the rest of the
  homelab. Makes ConnectionWatcher a citizen of a broader monitoring stack.
- **Healthchecks heartbeat (watch the watchman)** — ping a dead-man's-switch
  (healthchecks.io or self-hosted) every few minutes so a dead NAS/container/
  app produces an alert instead of silence. Small effort, closes a real blind
  spot: today, monitor-down looks identical to all-healthy.
- **ISP evidence report** — an export formatted for a support call to the
  internet provider: outage log with timestamps, loss evidence, weather-vs-
  link attribution from the verdict history, uptime summary for the billing
  period. Builds on the measurement-data export item above.
- **MCP server** — expose query tools (`current_status`, `outages_since`,
  `weather_history`, maybe `run_traceroute`) so Claude can answer "how was my
  internet last night?" from the actual database, or triage a slow-feeling
  connection on request.

## Still on the roadmap

- **Phase 4** — Docker packaging for Synology (traceroute needs NET_RAW),
  webhooks + MQTT toward Home Assistant (MQTT discovery).
