# Evidence-Based Verdict, Model Warnings and Critical Targets — Design

**Date:** 2026-10-09
**Status:** Draft for review
**Trigger:** 2026-10-08 23:42:16–23:43:08Z on the home NAS instance. "Business Router Inside" (10.1.10.1, `tcp/80`, a path hop) lost 15 of 20 probes. It went Down, every target beyond it was parked Unreachable, and the verdict said **Your internet is DOWN — Comcast is not carrying your traffic** for 27 s. The engine's own corroboration burst had just seen the anchors answer 35 of 36 pings, and all of them answered within seconds of the release. The internet was almost certainly up; one hop's *probe* failed.

## 1. The principle

> Neither a ping reply nor a TCP connection proves a node forwards packets. The only proof that packets are forwarded is that they reach **the next thing on the path**.

Today the engine reads "hop A's probe failed" as "the path through A is broken". It then silences everything beyond A, which destroys the evidence that would contradict it. The code states the rule outright: *"A Down ancestor always outranks anchor evidence."* This design reverses it. **Evidence from farther away outranks evidence from nearer.**

Corollaries (from the 2026-10-08 discussion with Mark):

1. **Path vs anchors.** If the path says down but the anchors are answering, the path model is wrong somehow. The verdict is *up*.
2. **Hop vs hop.** If A comes before B on the outbound path and A is not answering while B is, A's probe is misleading, not A's forwarding.
3. **Loss vs loss.** Real forwarding loss at A shows up at everything beyond A. A's *forwarding* loss therefore cannot exceed the lowest loss seen beyond it. A's measured loss above that figure belongs to A's own probe (rate-limiting, a busy control plane, a slow service), not to the path.

"The path model is wrong somehow" means ConnectionWatcher's map of the nearby network differs from the real one. In practice that is one of three things:

1. **The map is wrong:** the node isn't really on the path, or its probe measures a service and not reachability.
2. **The nearby network has redundancy:** for example, more than one connection from the point of presence.
3. **There is more than one internet path:** for example, a cellular fallback.

None of these is an internet problem. They are ConnectionWatcher problems, for the ConnectionWatcher administrator to know about.

## 2. Decisions already made (Mark, 2026-10-08/09)

| # | Decision |
|---|---|
| D1 | The headline verdict is decided by **end-to-end evidence** (anchors). The path never declares the internet down by itself; it explains *where* when the anchors say it is down. |
| D2 | **"Up" needs one anchor outside your ISP.** The internet is up when **at least one off-net anchor** is answering. An anchor run by your own ISP doesn't count, because it can stay reachable while the ISP has lost the rest of the internet. When ConnectionWatcher can't tell which anchors belong to the ISP (nothing on the path carries an operator tag), it needs **2 independent anchors** instead (§4.1). |
| D3 | A wrong path model is **not an internet problem**. It shows as a small badge and a message on the dashboard. It sends no toasts, webhooks or MQTT state changes, and doesn't touch the verdict. "The ConnectionWatcher administrator needs to know; nobody else should be bothered." |
| D4 | **No more inferred Unreachable.** A target is never marked unreachable until its *own* probes have proven it unreachable, independent of anything earlier on the path. |
| D5 | **Reach beyond immediately.** When something on the path changes state (fails *or* recovers), probe what lies beyond it at once rather than at normal cadence. |
| D6 | **Critical targets.** A target can be marked *critical*: we report when it is down even if everything else looks fine. The automated setup marks the router critical, and adopting a *router/gateway* discovery defaults to critical. Existing installs are not changed. |
| D7 | **Don't cry wolf.** One real failure must produce one alarm, not one per downstream target (root-cause grouping, §4.4). |

## 3. Goals and non-goals

**Goals**
- The 2026-10-08 incident produces **no internet-down verdict**. It produces a *model warning* naming "Business Router Inside" and the node beyond it that was still answering.
- A real outage (router dead, ISP down) is still detected about as fast as today, about 25–30 s, and blamed on the right hop.
- One root cause produces one notification.
- The administrator learns about misleading hops and gets a suggested fix.
- Critical devices are reported even when the internet is fine.

**Non-goals**
- Discovering redundant paths or multi-WAN automatically. We only *detect* that the model is contradicted.
- Changing the probe types (ICMP, TCP), the profiles or the threshold machinery of a single target.
- Rewriting stored history. Old `ParentDown` transitions keep their meaning in reports.
- Statistical loss attribution changing any state. Corollary 3 is used **only to annotate and to blame** (§4.5).

## 4. Design

### 4.1 Anchor evidence decides reachability

- An anchor is **answering** when its state is `Responding`, `Faltering` or `Recovering`, all of which mean it got replies in its window. `Down` is not answering. `Initializing`, `Starting` and an anchor with no fresh data are *undetermined*.
- **Independent anchors** are counted by operator, exactly as weather already does (`CountOperators`): anchors with the same operator tag count once, and untagged anchors count individually.
- **The ISP.** The ISP's operator is the operator tag(s) found on the **path hops** (for example "Comcast" on the edge). If no path hop has an operator tag, the ISP is **unknown**.
- **On-net anchors.** An anchor whose operator tag matches the ISP is **on-net**. It can stay reachable while the ISP has lost the rest of the internet, so it **never proves "up"**. It still counts for weather and site-incident classification. Tagging an anchor with the ISP's operator is how the administrator marks it as ISP-owned.
- **Required count:**
  - ISP known: **1** off-net anchor answering is enough. Any anchor other than the ISP's own will do.
  - ISP unknown: on-net and off-net can't be told apart, so **2 independent anchors** must answer (independent = distinct operators, untagged anchors count individually). With only one independent anchor configured in that case, one is used and the `UnprovableUp` warning says why.
  - ISP known but **no off-net anchor configured** (every anchor is the ISP's): the ISP's own anchors are used as a weaker fallback, and `UnprovableUp` says so.
- The config page and the setup wizard show the same advice as `UnprovableUp`: *"Tag your ISP on a path hop (or add an anchor outside your ISP) so ConnectionWatcher can prove your internet is up."*

### 4.2 The verdict, new rules

The verdict states keep their names, and every consumer already understands them. What changes is *when* each fires.

| Anchor evidence (independent, off-net) | Verdict | Blame |
|---|---|---|
| none determined yet | `Unknown` | – |
| answering ≥ required, none degraded | `Healthy` | – |
| answering ≥ required, some degraded, fewer than the weather threshold | `SiteIncident` | – |
| answering ≥ required, at least the weather threshold degraded, **and** the loss is attributed to our own path (§4.5) | `LinkDegraded` | deepest path hop carrying real forwarding loss |
| answering ≥ required, at least the weather threshold degraded, no path attribution | `InternetWeather` | – |
| 0 < answering < required | `LinkDegraded` ("only N independent anchors answering") | frontier hop (§4.3) |
| answering = 0, at least one determined | `LinkDown` | frontier hop (§4.3) |

**Path hop states never produce `LinkDown` or `LinkDegraded` on their own.** A Down or Faltering path hop while the anchors answer is a **model warning** (§4.6).

### 4.3 Blame: last good hop, first bad hop

When the anchors say down or degraded, walk each anchor's path outward from the root.

- A hop has **proven forwarding** if it, *or anything beyond it on that path*, is answering.
- The **frontier** is the farthest hop with proven forwarding. The **blamed hop** is the first hop after the frontier that is not answering. If every path hop answers and only the anchors fail, blame "beyond your last hop" and use the operator of the last path hop.
- Hops that are not answering but sit *before* a hop with proven forwarding are **skipped for blame** and raise a model warning.
- The banner names the blamed hop's operator tag, as today ("Comcast is not carrying your traffic"). It now only says so when the anchors agree the internet is down.

### 4.4 No inferred Unreachable; root-cause grouping

- **Every target keeps probing on its own schedule, whatever its ancestors are doing.** The parked state in `PingDiscipline` goes away.
- A target's state comes only from its own probes. It goes `Faltering`/`Down` on its own evidence.
- **Unreachable is redefined:** a target is `Unreachable` when its own probes show it **Down** *and* an ancestor is also Down **without proven forwarding** (§4.3). That is two observed facts, not an inference. The `ParentDown` trigger stays, with `AncestorId` naming the **root cause**: the shallowest such ancestor on its path, which is also the blamed hop when the anchors agree. A Down ancestor that traffic is visibly passing (a bypassed hop) never explains anything: a target behind it that is Down is plain `Down`, its own problem. Visuals stay as they are (the hatch: "explained, no blame").
- When the root cause recovers, an `Unreachable` target that is answering again leaves through `Recovering`. One that is still failing becomes plain `Down`, now its own problem.
- **Grouping:** anything that notifies or counts outages handles a `ParentDown`/`Unreachable` transition as part of its root cause's incident. That covers the tray, webhooks, MQTT transitions, the ISP report and glitches. One router failure is one incident.
- **Webhook:** transitions carry `explainedBy` (the root-cause target id and name). A setting "Skip explained transitions" (default **on**) suppresses `Unreachable` and `ParentDown` deliveries. The existing "verdict only" setting is unchanged.

### 4.5 Reach beyond immediately; loss attribution

- **D5:** when any path hop goes `Faltering` or `Down`, the engine immediately bursts every target beyond it, all anchors included. It does the same when a hop leaves `Down` or `Faltering`. The corroboration sweep (`SuspicionCoordinator`) already bursts on a first lost ping. This makes it a rule: contradictions are detected within seconds, not at normal cadence.
- **Loss attribution (corollary 3)** is computed only from **burst-aligned windows**: samples from the same sweep, the same time span, and a minimum sample count per target.
  - A hop's *forwarding loss* is the lower of its own loss and the lowest loss among the answering targets beyond it.
  - *Probe-only loss* is its own loss minus its forwarding loss.
  - It is used for `LinkDegraded` versus `InternetWeather` (§4.2): the loss belongs to our own path when a path hop's forwarding loss is at least the anchors' typical loss. It is also shown in the `ProbeOnlyLoss` note ("loss 75 % · forwarding 0 %"), not on the dashboard row.
  - **It never changes a target's own state.**

### 4.6 Model warnings (admin-facing)

A **model warning** says ConnectionWatcher's map disagrees with what the probes prove. Kinds:

| Kind | Condition | Message (sketch) |
|---|---|---|
| `HopBypassed` | A path hop is `Down`/`Faltering` while something beyond it is answering | "Business Router Inside isn't answering its probe (tcp/80), but Business Router External beyond it is — traffic is passing it. Its probe may measure a service rather than forwarding, it may not really be on the path, or your network has another route." |
| `ProbeOnlyLoss` | Over the last N burst-aligned windows a hop's probe-only loss stays ≥ X while its forwarding loss is about 0 | "Router drops some of its own pings (25 %) but forwards fine — common router behaviour; consider a TCP probe." |
| `NeverAnswered` | A path hop has not answered once since it was added or since startup, for at least T, while something beyond it answers | "Comcast Edge has never answered. Change its probe or remove it from the path." |
| `UnprovableUp` | The ISP is unknown and fewer than two independent anchors are configured, or no off-net anchor is configured | as in §4.1 |

- A warning is **active** while its condition holds and **clears itself** when it doesn't. Every warning start and end is logged and stored, so History can show it later.
- **Acknowledge** (admin-locked REST): hides the badge for that warning until it clears and recurs. It never disables the check.
- **Where it shows (D3):**
  - A small badge on the dashboard header (a wrench, with a count).
  - A "ConnectionWatcher notes" panel listing the active warnings.
  - A **subtle badge on the tray icon** (a small overlay dot, no colour change, no toast), with the active notes listed in the tray tooltip and a menu item that opens the dashboard's notes panel.
  - `/api/v1/status` gets `warnings[]`. MCP `get_current_status` gets `warnings`, which are read-only.
- **Where it never shows:** the verdict, tray toasts or the tray icon's colour, webhooks, MQTT state or availability, the ISP report outage counts, and metrics `up`/verdict series. A `conwatch_model_warnings` gauge is acceptable.

### 4.7 Critical targets

- A new `Critical` flag (bool, default `false`) on `Target`. It goes through the DTOs, the Targets dialog (a checkbox: "Critical — notify when this is down, even if the internet is fine"), export, import, clone and MCP status. EF migration `AddTargetCritical`: the column defaults to false, so **existing installs are unchanged** (D6).
- **Setup wizard:** the router target is created with `Critical = true`.
- **Discoveries:** adopting a `Gateway` discovery pre-checks Critical in the Targets dialog. Other kinds stay unchecked.
- **Behaviour:** a critical target's own `Down` (or `Unreachable`) and its recovery are *reportable events* even while the verdict is Healthy.
  - **Tray:** a toast, "Router is not answering (internet still up)" or "Router is answering again". This is an exception to D3, because a critical device is something the user chose to be told about.
  - **Webhooks:** delivered even with "verdict only" on.
  - **MQTT:** the per-target binary sensor already exists; its attributes gain `critical`.
  - **Dashboard:** the row shows the state chip prominently. Critical targets never change the verdict.
- **Critical plus a model warning.** A critical path hop that is Down while traffic passes produces **both** the critical notification (the device is not answering, which is what was asked) and the `HopBypassed` warning (explaining why the internet stayed up). The toast text says so.
- Local-scope targets (LAN devices) can be critical too (a NAS, a switch).

### 4.8 Warm start

`WarmStartSeeder` keeps seeding `Unreachable` only behind a seeded `Down` ancestor. Under D4 the seeded `Unreachable` is a presumption that the target's own fresh probes must confirm. They confirm it (stays Unreachable) or correct it, the same way a seeded state works today.

## 5. What changes, by component

| Component | Change |
|---|---|
| `ConEngine/Topology/VerdictCoordinator` | New classification (§4.2): anchor-first, on-net guard, required count, frontier blame, loss attribution input. Drop "Down ancestor outranks". |
| `ConEngine/Topology/TopologyCoordinator` | No longer calls `SetUnreachable` on descendants. Instead it (a) triggers the D5 burst of descendants on a hop's Faltering/Down and on its recovery, and (b) computes root causes: a descendant whose own state goes Down while an ancestor is Down becomes `Unreachable` with `AncestorId` = the root cause. |
| `ConEngine/Tracking/TargetMonitor`, `HostAnalysisEngine` | `MarkUnreachable` becomes "explain my Down"; `Recalculate` keeps running for Unreachable targets (it no longer returns null); `Release` becomes "re-classify" on root-cause recovery. |
| `ConEngine/Scheduling/PingDiscipline` | No parking: Unreachable pings like Down (burst-capped). |
| `ConEngine/Topology/SuspicionCoordinator` | Provides burst-aligned windows for loss attribution; the D5 burst trigger. Glitch correlation attributes descendant glitches to the root cause. |
| new `ConEngine/Topology/ModelCheckCoordinator` | Evaluates the §4.6 warnings from monitor states and burst windows, and emits warning start/end events. |
| `ConCore/Targets/Target` + persistence + DTOs + validator + export/import/clone | `Critical` flag, migration `AddTargetCritical`. |
| `ConWatch` status API, MCP `get_current_status` | `warnings[]`; `critical` and `explainedBy` per target. |
| REST (locked) | `POST /api/v1/warnings/{key}/acknowledge`. |
| Setup wizard, Discoveries adopt | Router critical; Gateway adopt pre-checks Critical. |
| Dashboard (React) | Header badge, notes panel, critical marker. No per-row warning chip. |
| Tray | Critical toasts (§4.7). Model warnings: a subtle icon-overlay badge, tooltip list and "ConnectionWatcher notes" menu item; never a toast. The status API's `warnings[]` feeds it. |
| `ConNotify` webhook / MQTT | `explainedBy`; "Skip explained transitions" setting; critical passes "verdict only"; MQTT target attributes `critical`. |
| ISP report | Outages counted by the (new) verdict; Unreachable time is attributed to the root cause. Old history unchanged. |
| Docs | README section on how the verdict is decided; SECURITY.md is unchanged (warnings are read-only on the open surface, like status). |

## 6. Edge cases

- **Router truly dead.** Anchors go Down within the burst (about 25 s), the verdict is `LinkDown`, and the blame is the router (frontier = nothing answering). Descendants prove themselves Down, become `Unreachable` explained by the router, and send one notification. Same speed as today, and now with proof.
- **ISP outage beyond the modem.** The modem and edge answer and the anchors fail: `LinkDown`, blaming the first silent hop after the edge, or "beyond your last hop" with the operator of the last path hop.
- **This incident.** Inside is Down while External and the anchors answer: verdict `Healthy` plus a `HopBypassed` warning on Inside, no toast. If Inside were critical, there would also be a critical toast that says the internet is up.
- **Cellular failover.** The primary edge is Down and the anchors answer through the backup: verdict `Healthy` plus `HopBypassed` on the edge. The administrator sees "your network has another route". If the edge is critical, they are also told it is down.
- **ICMP-deprioritising router.** Steady 20–40 % loss on the router and 0 % beyond: `ProbeOnlyLoss`, which never degrades the verdict.
- **A hop that never answers** (ISP gear blocking ping): `NeverAnswered` until fixed or acknowledged. It no longer parks anything.
- **Only on-net anchors configured** (all tagged with the ISP's operator): the ISP's own anchors are used as a weaker fallback (§4.1), and the `UnprovableUp` warning says so.
- **ISP not tagged anywhere:** two independent anchors must answer; with four untagged anchors that is any two of them.
- **Anchor flapping at the boundary of two.** The anchors' own hysteresis (Faltering/Recovering thresholds) already damps this, and the verdict doesn't add a second hysteresis.
- **Warm start mid-outage.** §4.8.

## 7. Testing

- **Engine unit tests (ConEngine.Tests)**, with fake probes and a fake time provider, no sleeps:
  - The incident replay: a hop goes Down while descendants and anchors answer, giving `Healthy` and `HopBypassed`, with no `LinkDown` ever emitted.
  - Router dead.
  - ISP down beyond the edge.
  - The cellular-failover shape.
  - On-net anchors not counting.
  - Required count: ISP known (1 off-net anchor suffices, ISP-tagged anchors never count), ISP unknown (2 independent needed), no off-net anchor configured (fallback plus warning).
  - Frontier blame with branching anchor paths.
  - Root-cause grouping: N descendants produce one root, and `explainedBy` is set.
  - Recovery bursts.
  - Probe-only loss annotation never changes state.
  - Warning start/end/acknowledge.
- **Persistence:** the `Critical` migration, and export/import/clone round-trip.
- **API/MCP:** `warnings[]`, `critical`, `explainedBy`; acknowledge is locked; `ApiSurfaceLockTests` stays green.
- **Notifiers:** "Skip explained transitions"; critical passes "verdict only"; MQTT attributes.
- **Tray:** `TrayStatus` mapping and critical toast text (logic in a testable library where possible).
- **UI (vitest):** header badge, notes panel, critical marker. **Tray:** overlay badge and tooltip text from `warnings[]` (logic in a testable library).
- **Replay check:** feed the 2026-10-08 transition and rollup sequence into the engine harness and assert the new outcome.

## 8. Rollout

- One spec, but the plan should split it so each step is releasable on `edge`:
  1. Engine: no parking, own-evidence Unreachable, D5 bursts, root-cause grouping (behaviour-compatible verdict).
  2. The new verdict classification and frontier blame.
  3. Model warnings plus the dashboard badge and panel.
  4. Critical targets.
  5. Loss attribution and `ProbeOnlyLoss` (the most statistical part, last).
- README gains a short "How the verdict is decided" section. The ISP report wording is checked against the new outage semantics.

## 9. Decisions on the open questions (Mark, 2026-10-09)

1. **Critical LAN devices toast:** yes. Critical means "tell me".
2. **"Skip explained transitions"** for webhooks: **on** by default.
3. **ProbeOnlyLoss:** explained to Mark on 2026-10-09; decision pending (see the conversation). Until decided, the plan treats it as the last, separately switchable step.
4. **Acknowledge** hides that instance until it clears and recurs. The way to silence a hop for good is to fix the model.
