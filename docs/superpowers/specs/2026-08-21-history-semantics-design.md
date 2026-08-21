# History Semantics — Outages, No-Data Time, and Address Changes

**Date:** 2026-08-21
**Status:** Approved (designed in conversation from a user-reported anomaly)

## Principles (user-stated)

1. **Unreachable means no data.** While a target is `Unreachable` the engine
   does not ping it; we know nothing of value. It is a coverage gap, never a
   judgment about the target.
2. **An address edit means one of three things:**
   1. Correcting a bad address (the old series measured the wrong thing);
   2. Pointing at a different device/interface (the old series measured a
      different thing);
   3. The same interface got a new address, e.g. an ISP renumber (one
      continuous series).
   Cases 1–2 imply a **break** between data histories; case 3 implies
   **continuity**. Only the user knows which — so the user is asked.

## Decisions

| Topic | Decision |
|---|---|
| Stats split | History stats separate **outages** (`Down`: count + longest) from **unreachable — no data** (total time). Unreachable windows end at the *release* (`ParentRecovered`), not at the end of re-proving. |
| Address edit UX | Editing a target's address prompts the three-way question in the Config UI. The REST API requires `addressChange` = `correction` \| `repoint` \| `renumber` on any PUT that changes the address; omitting it is a 400 naming the values (explicit over silent defaults). |
| Markers | The config service records a marker transition row at commit: Trigger **`HistoryReset`** for correction/repoint, **`AddressChanged`** for renumber; `From=To=Starting`; detail "old → new (kind)". Nothing is ever deleted. |
| Stats clipping | Uptime, outages, unreachable time, average RTT, and the charts are computed only from rollups/transitions **after the latest `HistoryReset`**. |
| Ribbon | The ribbon still draws the pre-reset era, but dimmed, with "(before address change)" in the tooltip — visible history, zero attribution. |
| Renumber limitation | For case 3, any `Down` measured against the stale address stays counted (during that window the interface genuinely could not be verified); the marker is present for human interpretation. |
| Import | Whole-config import is backup-restore, not editing: it records no markers and implies continuity. |

## Touch points

- `ConPersistence.ConfigService.UpsertTargetAsync` — addressChange validation + marker rows (gains a TimeProvider).
- `PUT /api/v1/targets/{id}?addressChange=` — pass-through, 400 on missing/invalid when the address changed.
- `ClientApp/src/lib/historyData.ts` — `incidents()` split + `clipAtReset()`; ribbon reset awareness.
- `StatsRow` / history island — new tiles, clipped inputs.
- `Pages/Config/Targets.cshtml` — three-way dialog on address edits.
