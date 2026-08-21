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

## Still on the roadmap

- **Phase 4** — Docker packaging for Synology (traceroute needs NET_RAW),
  webhooks + MQTT toward Home Assistant (MQTT discovery).
