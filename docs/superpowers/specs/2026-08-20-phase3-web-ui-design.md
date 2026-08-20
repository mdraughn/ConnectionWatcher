# ConnectionWatcher Phase 3 — Web UI Design

**Date:** 2026-08-20
**Status:** Approved design, pending implementation plan
**Prerequisites:** Phase 1 engine and Phase 2 persistence + API (both merged to master).

## 1. Purpose

Give ConWatch a face: a live dashboard that answers "is my internet OK?" at a
glance, history pages that answer "how has it been?", config editors that
replace hand-edited JSON, and a traceroute assist that discovers upstream hops
worth monitoring. All served by the existing single ConWatch process.

## 2. Decisions made during design review

| Topic | Decision |
|---|---|
| Page scope (v1) | **All four pages**: Dashboard, History & charts, Config editors, Traceroute assist. |
| Styling | **Pico.css** (classless, auto dark/light) + one small custom stylesheet (status colors, tree, tiles). |
| Dashboard layout | **Verdict banner + calm tree; trouble expands in place** (user-designed): misbehaving rows smoothly expand into tiles with a 60-minute chart, bottom status bar shows the latest event, slide-up panel for recent events. |
| Expansion rule | **Rule C**: automatic on non-quiet states with a 5-minute cool-down after recovery, plus click-to-pin any row (pins persist in localStorage). Initial `Starting` on fresh boot stays collapsed; `Starting` after release from `Unreachable` expands. |
| History charts | **All four**: latency band (avg + min–max), loss-rate bars, state timeline ribbon, uptime summary stats row. |
| Islands/build | **Approach A**: React + TypeScript + TanStack React Charts, built by Vite into `wwwroot/js/`, wired into `dotnet publish`. Island boundary isolates the beta-chart risk; named fallback is uPlot inside the same boundary. |
| Live updates | **SignalR** hub `/hubs/status` fed by a `StatusBroadcaster`. |
| Auth | None (unchanged; LAN appliance — Phase 4 revisits exposure). |

## 3. Architecture

### SignalR

- Hub: `/hubs/status`. Messages:
  - `statusChanged` — the same snapshot shape as `GET /api/v1/status`;
    throttled to ≤1/second, but sent immediately when any transition fires.
  - `transition` — one TargetTransition DTO per event.
- `StatusBroadcaster` (ConWatch hosted service) subscribes to the engine's
  `TransitionSource` and `Monitors` statuses, re-attaching across engine
  rebuilds (same pattern as TransitionRecorder).
- Clients auto-reconnect; on reconnect they re-fetch `/api/v1/status` to
  resync — pushed deltas are never trusted across a gap.

### Islands and build pipeline

- `Programs/ConWatch/ClientApp/`: Vite + React + TypeScript.
  - Entries: `dashboard.tsx` (live tree island), `history.tsx` (charts island).
  - Shared modules: SignalR client wrapper, TanStack chart components,
    status-merge reducer, expansion-rule state machine (pure, unit-tested).
- `vite build` emits bundles to `Programs/ConWatch/wwwroot/js/`.
- The csproj runs `npm ci && npm run build` on `dotnet publish`, and on normal
  builds when the output bundles are missing. A Vite build failure fails the
  .NET build loudly.
- Dev loop: `vite build --watch` beside `dotnet run`.

### Pages

Razor Pages, shared layout, top nav **Dashboard · History · Config ·
Traceroute**. Every page renders meaningful server-side HTML first (dashboard
renders the real tree from live engine state at request time); islands hydrate
and add liveness. Config and Traceroute pages use plain Razor + small vanilla
fetch helpers — no React.

## 4. Dashboard

- **Verdict banner**: color + text from the verdict state and its Detail
  (e.g. "SITE INCIDENT — Cloudflare degraded; your link is fine"). Absent when
  the verdict layer is disabled.
- **Tree**: targets nested by parent; pools listed with derived state.
  Collapsed row: status dot, name, success rate, avg RTT — live-updated.
- **Expansion (rule C)**:
  - Quiet set: `Responding`, plus initial `Starting` on fresh boot.
  - A row expands while its state is outside the quiet set; after returning to
    `Responding` it stays open for a 5-minute cool-down, then collapses.
  - Clicking a row pins it open (click again unpins); pins in localStorage.
  - Smooth height transitions (CSS).
- **Expanded tile**: state, the "why" line from the latest transition
  (evidence string), burst-probing indicator, and a 60-minute chart —
  minute-resolution rollups fetched on expand and re-fetched once per minute
  while open. Chart: latency line with loss shading (shared TanStack component).
- **Status bar** (fixed, bottom): timestamp + text of the latest transition;
  "show all events" slides up a panel with the last 50 transitions
  (API-fetched, live-prepended).
- **No-JS fallback**: the server-rendered tree is correct at load time.

## 5. History

- Pickers: target/pool/verdict subject; range 24 h / 7 d / 30 d / custom.
- Stats row (client-computed): uptime % (from rollups), incident count and
  longest outage (from transitions).
- Three visuals sharing one time axis:
  1. **Latency band** — avg line, min–max shading (TanStack, `/rollups/{id}`,
     `resolution=auto`).
  2. **Loss-rate bars** — per-period loss % (TanStack, same data).
  3. **State timeline ribbon** — hand-rolled SVG from
     `/transitions?subjectId=`: colored stripes with hover tooltips showing
     each transition's evidence.
- Pools and the verdict subject have no rollups: ribbon + transition-derived
  stats only. The verdict ribbon is the "internet weather history" view.

## 6. Config editors

- Three pages: Targets, Profiles, Pools. Table + edit form each.
- Fields: name, address, parent dropdown, profile dropdown, enabled toggle;
  pools get an orderable member list.
- Fetch-based submits to the existing `/api/v1` CRUD; 400 validator messages
  render inline; 409 conflict messages shown verbatim.
- Banner: "changes apply within seconds — the engine rebuilds automatically."
- Export (download JSON) / Import (upload with confirmation) buttons.

## 7. Traceroute assist

- New `TracerouteRunner` in ConInfrastructure: .NET `Ping` with stepped TTL
  (1–30, 3 probes per hop, 2 s timeout), returning per-hop address, RTT, and
  reverse-DNS name when available. Ends at destination or TTL 30.
- `POST /api/v1/traceroute { host }` — single response when complete; the page
  shows a spinner meanwhile.
- Results table with per-hop **"add as target"**: opens the target form
  pre-filled with the hop IP and parent defaulted to the nearest earlier hop
  that is already a monitored target.
- **Phase 4 note:** ICMP with TTL control in a Linux container requires
  `NET_RAW`; the packaging spec must grant it.

## 8. Error handling

- SignalR disconnect: banner dims with a "reconnecting…" pill; resync via
  `/status` on reconnect.
- Island API errors render inline messages (no silent failures).
- Traceroute failures show a readable message and keep partial results.
- Vite build failure fails `dotnet publish`.

## 9. Testing

- **xUnit (ConWatch.Tests)**: StatusBroadcaster throttling + rebuild reattach;
  SignalR integration — WebApplicationFactory + HubConnection receives
  `transition` during a simulated outage; TracerouteRunner against a fake
  pinger (synthetic TTL-expired replies); traceroute endpoint contract.
- **Vitest (ClientApp)**: expansion-rule state machine (quiet set, cool-down,
  pins); status-merge reducer; chart component render-smoke only.
- **Manual checklist** against fake pings in Development.

## 10. Exit criteria

1. During a simulated router outage, an open dashboard — without refresh —
   shows the verdict banner flip, affected rows expand into tiles with charts,
   the status bar narrates, and everything collapses back after recovery +
   cool-down.
2. Pin/unpin works and survives a page refresh.
3. History renders stats + all three visuals for a real target, and the
   ribbon for the verdict subject.
4. A target created through the Config UI is being pinged within seconds; an
   invalid edit shows the validator's message inline.
5. Traceroute discovers the real upstream path; one click adds a hop as a
   target with the right parent.
6. `dotnet publish` produces the complete app (bundles included); all suites
   green.

## 11. Out of scope for Phase 3

- Docker packaging, MQTT/webhooks, Home Assistant (Phase 4).
- Authentication/TLS (Phase 4 decision).
- Continuous/scheduled traceroute monitoring (explicitly rejected in Phase 1
  design review).
- Mobile-specific layouts (Pico is responsive enough for v1).
