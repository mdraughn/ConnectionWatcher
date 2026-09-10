# ISP Naming & Clone-Install / Switch-Over — Design Notes (pre-interview)

**Date:** 2026-09-10
**Status:** Thinking notes per Mark's request ("think about that while I'm
gone"). Open questions at the end need his answers before spec/plan.

---

## Idea 1: Name the ISP ("Comcast is down")

### What it buys

Every surface that today says "trunk" or "LinkDown" can name names:
- Verdict detail: "LinkDown — **Comcast** is not carrying traffic"
- Incident ledger: "Tuesday 12:38 AM — **Comcast** went down for 4 minutes"
- ISP report: titled and attributed per provider — the report becomes
  "Evidence against Comcast" instead of "evidence about my internet"
- Dual-WAN future: "Failover: **Comcast** down, running on **Easy Fiber**"
- HA/MQTT: verdict attributes carry the operator name; toasts likewise.

### Where the name lives

The name is conceptually a property of a **WAN lane/path**, but lanes don't
exist as entities yet (they arrive with the failover-group work). Two-step
plan that avoids inventing the lane entity early:

1. **Now:** optional `Operator` (string) column on Target + DTO + config UI
   field + wizard question ("Who is your ISP?" — free text, prefilled by
   guessing from the WAN address's WHOIS org? keep manual for v1). Tag the
   trunk targets: External Router + Comcast Edge → "Comcast". Attribution
   surfaces use the operator of the deepest blamed trunk target when
   present, else today's wording.
2. **Later (failover groups):** the lane entity gets the operator name and
   member targets inherit it; the column migrates naturally.

Cheap, non-breaking, and the display redesign (ledger sentences) consumes it
directly.

---

## Idea 2: Clone-install + master enable = switch-over as an install path

### The observation

`/api/v1/export/snapshot` + `SnapshotImporter` already move the complete
sanitized state between machines. That IS a migration path — the PC→NAS move
that lost the PC-era history would have been lossless with this. Two pieces
are missing to make it a first-class install path:

1. **Clone on first boot:** the setup wizard gains a third option —
   "Copy from a running ConnectionWatcher" (enter its URL) → fetch snapshot →
   import → arrive fully configured with history intact.
2. **Master enable switch:** a per-node "monitoring enabled" toggle so two
   nodes never both actively monitor. Disabled = engine parked (no pings, no
   MQTT/webhook/heartbeat publishes), **web server and API stay up** — a
   disabled node still serves its dashboard (read-only view of history) and,
   critically, still serves `/export/snapshot`.

### Switch-over choreography (the whole point)

The clean sequence, driven from the NEW node's wizard:

1. New node clones from old node (both running; old still monitoring).
2. Human verifies new node looks right (targets, history, charts).
3. **Take over**: disable old node → pull one FINAL fresh snapshot (captures
   everything up to seconds ago — near-zero data gap) → import → enable new
   node.
4. Old node remains as a disabled, browsable archive until decommissioned.

Ordering notes:
- Disable-before-final-sync is what makes the gap ~seconds and prevents
  overlap (no double data, no MQTT entity fights, no double heartbeat).
- MQTT: old node's disable should publish `offline` (LWT semantics) before
  the new node connects, so HA entities transfer cleanly.
- Heartbeat: the check service sees at most a seconds-long gap — below any
  sane alert threshold.

### What deliberately does NOT transfer

- **Secrets** (MQTT password; any webhook/heartbeat URLs) — snapshot
  redaction is by design. The switch-over checklist surfaces "re-enter MQTT
  password on the new node" before enabling. This is a feature: the moment
  of migration re-confirms the credentials.
- **Admin password file** — per-node by design (delete-the-file reset story
  must stay per-machine).

### Implementation shape (rough)

- Settings key `Engine.Enabled` (default true) + `POST /api/v1/admin/enable`
  (admin-gated); WatcherHostService honors it on (re)build: enabled=false →
  no engine start, notifiers idle; SettingsChanged-style live toggle.
- Wizard third path + a "Take over from source" action that calls the OLD
  node's disable endpoint (needs the old node's admin auth if gated — the
  flow must prompt for it or ask that the gate be opened) then final-syncs.
- Snapshot import already exists; wizard wraps `SnapshotImporter` behind an
  endpoint (POST /api/v1/setup/clone {url}) running server-side on the new
  node.
- UI truthfulness: a disabled node's dashboard shows a prominent
  "monitoring disabled — archive mode / cloned to <url>?" banner, not a sea
  of gray targets.

### Risks / edges

- Both-enabled overlap (user error): mostly harmless (duplicate pings, MQTT
  entity fights) — a health warning "another node may be active" is hard to
  detect reliably; document instead, or check: clone stores source URL and
  the new node can probe whether source is still enabled and warn.
- Snapshot schema versioning already exists (`schemaVersion` guard).
- Cross-version clone (old node older than new): importer tolerates missing
  collections (empty lists); config validation is the gate. New→old refuses
  via schemaVersion when we ever bump it.

---

## Open questions for Mark

1. **ISP name entry point**: per-target field only, or also a wizard
   question? And should the ISP report title use it ("Report on Comcast
   service")?
2. **Disabled-node dashboard**: archive mode banner + read-only history is
   the proposal — anything else it should still do (exports? reports?)?
3. **Take-over auth**: disabling the OLD node needs its admin password (if
   set). OK for the wizard to prompt for the source's password at take-over
   time (used against the source node only), or prefer "open the gate on the
   source first" as the documented flow?
4. Priority order: ISP naming is small and feeds the display redesign;
   clone/switch-over is medium and independent. Which first — or does
   display redesign (already chosen) stay ahead of both?
