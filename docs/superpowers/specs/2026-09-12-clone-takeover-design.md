# Clone-install + master enable switch (switch-over) — design

**Date:** 2026-09-12
**Status:** APPROVED (interviews 2026-09-10 and 2026-09-12; transport/auth
policy approved 2026-09-12 "Make it so").
Builds on: `2026-09-10-isp-naming-and-clone-install-notes.md`.

## Goal

Moving ConnectionWatcher to a new machine becomes a first-class install path:
clone a running node (config + full history + secrets), verify at leisure,
then take over with a seconds-long data gap. The old node stays behind as a
browsable read-only archive. The enabling primitive — a per-node master
enable switch — doubles as a plain "pause monitoring" control.

## Transport & auth policy (decided 2026-09-12)

The SOURCE cannot know it is behind HTTPS (TLS terminates at the reverse
proxy), and a server-side scheme check would inspect a password that already
crossed the wire — worthless. **Transport policy is enforced by the
REQUESTING node**, the only party that knows the transport end to end:

- `https://` source → proceed; certificates validate normally (no
  skip-verification knob — a TLS failure suggests using HTTP + override).
- `http://` + loopback → proceed silently.
- `http://` + anything else, when secrets or a password are involved →
  the wizard stops with an honest warning ("you're about to send the source
  admin password and receive its MQTT password/webhook URLs unencrypted"),
  offering: use the HTTPS address / continue anyway (explicit opt-in) /
  clone without secrets (sanitized snapshot; re-enter MQTT password later).
- The requesting node's server-side clone endpoint re-enforces this
  (`allowInsecure` flag set only by the wizard's opt-in), so the policy
  cannot be bypassed by accident.

Calibration matches the admin gate's stated bar: prevents casual mistakes,
not internet-grade security; a hard HTTPS requirement would break the
plain-LAN home case for no proportionate gain.

**Full-snapshot auth is single-shot**: `POST /api/v1/export/snapshot/full`
with `{ "password": "..." }` verified inline against the source's AdminGate
(401 on mismatch; allowed without password while the source gate is
disabled, consistent with the rest of the locked surface). No cross-node
cookie dance; nothing stored. The open `GET /api/v1/export/snapshot` stays
sanitized forever.

## Master enable switch

- Settings key **`Engine.Enabled`** (default `true`).
- `WatcherHostService.StartEngineAsync` checks it: disabled → engine is not
  built (pings, glitches, rollups, verdict all stop). The existing
  SettingsChanged → rebuild path makes it a live toggle. Re-enabling
  warm-restarts from the DB when within the gap window.
- **Disable flushes the recorders** before completing, so a snapshot taken
  right after disable contains everything up to that moment (the take-over
  final sync depends on this).
- **MQTT goes offline honestly**: on disable the notifier publishes its
  availability `offline` and disconnects — HA entities show unavailable,
  not stale states. Heartbeat pings pause while disabled (the watchman is
  deliberately off duty; the check service alerting is correct behavior —
  the checklist tells the user to pause or move the check).
- **Per-node keys never travel in snapshots**: `Engine.Enabled` and
  `Clone.SourceUrl` are excluded on export AND skipped on import (a final
  sync from a just-disabled source must not import `Enabled=false`).
- `/api/v1/health` while deliberately disabled: **200 `"Disabled"`** (an
  archive node is healthy as an archive; container healthchecks must not
  flag it). 503 stays for enabled-but-engine-down.
- **M2M control endpoint** for take-over: `POST /api/v1/admin/engine` with
  `{ "enabled": bool, "password": "..." }`, same inline-auth semantics as
  snapshot/full. The local Admin page toggle uses the normal locked
  settings PUT; this endpoint exists for the remote choreography.

### Archive mode (decided 2026-09-10: everything read-only)

A disabled node keeps serving everything DB-backed: History, ISP report,
exports, event strip, ledger, glitches. The dashboard replaces the verdict
banner with a prominent neutral **"Monitoring disabled — archive mode"**
banner and hides the live tree (no live states — no pretense); Admin shows
the toggle to re-enable.

## Clone (setup wizard, third option)

"Copy from a running ConnectionWatcher": URL + optional source admin
password (empty = source gate open). Server-side on the NEW node:

- `POST /api/v1/setup/clone { sourceUrl, sourcePassword?, includeSecrets,
  allowInsecure }` → transport policy check → fetch full (or sanitized)
  snapshot → `SnapshotImporter` into the fresh DB → store `Clone.SourceUrl`
  → **set `Engine.Enabled=false`**. A fresh clone arrives as a disabled
  archive: the source keeps monitoring, nothing double-pings, MQTT doesn't
  fight. Outbound HTTP goes through `IHttpClientFactory` (testable with a
  handler routed at a second in-memory app).
- Wizard copy explains the state: "cloned — verify, then take over."

## Take-over choreography

Post-clone, Dashboard/Admin on the new node show a "Cloned from
`<sourceUrl>`" card with **Take over** (locally admin-gated):

`POST /api/v1/setup/takeover { sourcePassword? }`:
1. Remote-disable the source (`POST <source>/api/v1/admin/engine
   {enabled:false, password}`) — source flushes recorders as part of
   disabling.
2. Pull one final full snapshot from the source.
3. **Import-replace** locally: inside one transaction, clear all data +
   config rows and import the final snapshot (per-node keys skipped). The
   local engine is disabled during this, so there are no concurrent
   writers.
4. Set `Engine.Enabled=true` locally → engine builds → the new node is live
   with a seconds-long gap. `Clone.SourceUrl` is kept for provenance and
   the both-active warning.

Failure handling: each step reports precisely (source unreachable, bad
password, import failure); nothing is destructive until step 3, and step 3's
transaction means a failed import leaves the pre-takeover clone intact. If
step 4 never runs because 1–3 failed, the user retries; the source was at
worst disabled and can be re-enabled from its own Admin page.

## Both-active warning

ConfigHealth gains a warning: when `Clone.SourceUrl` is set, this node is
enabled, and a short-timeout probe of the source's `/api/v1/health` says the
source is ALSO enabled → "the node you cloned from appears to still be
monitoring." Probe failures stay silent (an unreachable/decommissioned
source is the normal end state).

## Non-goals

- No TLS skip-verification knob.
- No "cloned to <url>" note on the source (its archive banner is generic).
- No auto-discovery of source nodes; the user types the URL.
- Admin password files remain strictly per-node (delete-to-reset story).

## Testing

- Pure transport-policy function unit-tested (https / http+loopback /
  http+LAN × secrets/no-secrets × override).
- Exporter/importer: full vs sanitized, per-node-key exclusion both ways,
  import-replace transactional semantics.
- Surface lock: snapshot/full and admin/engine are open-with-own-auth
  (SkipProbe, like /auth/login); everything else unchanged.
- Integration with two in-memory app factories: clone A→B (HttpClient
  handler routed at A), verify B arrives disabled with A's data; take-over
  flips A disabled / B enabled with A's final rows present; wrong source
  password → 401 surfaced; http-without-override → refused.
