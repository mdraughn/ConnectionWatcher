# Theory of Operation

How ConnectionWatcher decides what's up, what's down, and who to blame.
You don't need any of this to use the app — the defaults are tuned for a
home connection — but when you want to know *why* the banner says what it
says, or what a knob on the Profiles page actually does, this is the place.

## The big picture

ConnectionWatcher pings things. Specifically, it probes a set of **targets**
you configure:

- **The path to the internet** — your router, maybe your modem, maybe a hop
  or two of your ISP's equipment. These are arranged in a parent/child
  **tree** that mirrors the order packets traverse them.
- **Internet anchors** — a handful of reliable public sites (the defaults
  are the big anycast DNS services: Google, Cloudflare, Quad9, OpenDNS)
  that sit at the far end of the tree. They are grouped into a **pool**
  that shares the pinging load.
- **Local devices** (optional) — LAN gear you want to keep an eye on.
  These never affect the internet verdict.
- **Resolvers** (automatic) — the DNS servers your machine is configured
  to use, watched so a dead resolver doesn't masquerade as a healthy
  internet.

Every probe result feeds a per-target state machine. State changes feed a
**verdict layer** that answers the headline question — *is my internet
down, and what kind of down is it?* — and everything (states, transitions,
rollups, glitches, verdicts) is recorded so History and the ISP report can
show you what happened later.

The design principle running through all of it: **conclusions come from
evidence, and evidence from farther away outranks evidence from nearer.**
A router that ignores pings while visibly forwarding traffic is a router
with an attitude, not an outage.

## Probes

Each target is probed one of three ways:

- **ICMP ping** (the default). One echo request per cycle; the reply time
  is the RTT.
- **TCP connect** (`tcp:<port>`), for devices that firewall ping but run a
  service. Each cycle opens one fresh socket, sends no data, and closes
  cleanly. A **refused** connection still counts as a reply — an RST proves
  the host's stack answered and the path works; this tool asks "can I reach
  this device," not "is the service healthy." A host that silently drops
  the SYN reads as down, correctly. Port policy is polite by address class:
  private addresses (RFC1918, loopback, link-local) allow any port; public
  addresses are restricted to 80, 443, and 53, where a bare
  connect-and-close looks like ordinary client traffic.
- **DNS query** (`dns`), used for resolver targets. One real DNS question
  per cycle over UDP port 53; the answer time is the RTT. See
  [the name-resolution watch](#the-name-resolution-watch) below.

The Targets form's **Test** button tries ICMP and the policy-allowed TCP
ports for the typed address in one click and preselects whichever answered.

**Hostname targets follow DNS.** A target configured by name is re-resolved
every five minutes and immediately when it goes Down; an address that has
left the DNS answer set is replaced without resetting the target's history.
The Test button for a hostname also lists every address in the answer set,
so you can see what is being pinned.

## Ping cadence: profiles and bursts

Every target has a **profile** — a named bundle of timing and threshold
settings. Five ship built in: **Internal** (your own LAN: fast pings are
fine), **Path** (hops to the internet you don't own), **Public** (sites
where you should be polite — slow steady-state pings), **Local** (LAN
devices, tolerant of sleepy IoT gear), and **Resolver** (used by the
name-resolution watch). You can edit these or define more.

Each profile sets two cadences:

- **Normal interval** — the steady-state ping rate while a target is
  Responding (or long-term Down). The defaults: Internal 1 s, Path 5 s,
  Public a polite 10 s, Local 10 s, Resolver 30 s.
- **Burst interval** — a faster rate (typically 1 s) used for a bounded
  window (`BurstMaxDuration`, typically 60 s) when something interesting
  happens: the target's state changes, a suspicion burst starts (below), or
  the corroboration sweep fires. While bursting, the effective ping timeout
  is clamped down (about a second) so a dead target doesn't slow the
  evidence gathering.

Politeness invariants, by design: externals are never pinged faster than
their profile's burst interval even in a burst; bursts are always
time-capped, so no target is hammered through a long outage; pool members
share the load by phase-staggering their schedules (three anchors at 15 s
intervals yield a data point every 5 s while each site sees one ping per
15 s).

## Target states

Each target runs this state machine, driven by the success rate over a
sliding window of recent probes (default window 100 samples, with at least
10 required before judging; the thresholds default to Responding ≥ 0.95,
Faltering < 0.75, Down < 0.10, Recovering ≥ 0.25 — all editable per
profile):

```
Initializing → Starting → Responding ⇄ Faltering → Down ⇄ Recovering → Responding
                                plus Unreachable (explained Down — see below)
```

- **Starting**: collecting the first usable sample (`MinUsableSample`
  probes) before judging. New targets, re-resolved targets, and targets
  released after an explained outage pass through here.
- **Responding**: success rate above the Responding threshold.
- **Faltering**: success rate has dropped below the Faltering threshold —
  degraded, not dead. Entering Faltering starts a burst to measure the
  loss quickly.
- **Down**: success rate below the Down threshold. The first
  `BurstMaxDuration` of Down is burst-probed to catch a fast recovery; a
  long outage falls back to normal cadence.
- **Recovering**: a Down target whose replies are returning; it must climb
  past the Recovering threshold before it's Responding again. The
  separate up/down thresholds are deliberate hysteresis — a marginal
  connection flaps between Faltering and Responding, not between Down and
  up.
- **Unreachable**: the target's own probes prove it Down *and* something
  earlier on its path is Down without proven forwarding. It is an
  *explained* Down, not an inferred one — see
  [root-cause grouping](#unreachable-and-root-cause-grouping).

Every state change is recorded as a **transition** with a structured
reason: what triggered it (threshold crossed, parent down, resolution
failed…) and the numbers behind it (success rate, sample size, burst loss).
Transitions are the atoms that History, notifications, and the ISP report
are built from.

## Fast detection

Steady-state sampling is lazy on purpose — but trouble accelerates it:

- **Suspicion burst (per target).** A single failed probe on a target that
  was Responding starts that target's burst window immediately. One lost
  packet is not an event; it *is* a reason to look harder. (A politeness
  guard keeps a 3–5% lossy line from bursting forever: a new suspicion
  burst starts only if no burst is active and at least a minute has passed
  since the last one started.)
- **Corroboration sweep (cross-target).** A failed probe on an anchor or a
  trunk target (the anchor pool's parent chain to the root) triggers an
  immediate burst on **all** anchors and the whole trunk. If your
  connection really just died, ConnectionWatcher wants every witness
  testifying within seconds, not at their normal cadence. The same sweep
  fires when a path hop changes state in either direction — contradictions
  between a hop and what lies beyond it are detected within seconds.

Net effect with the default profiles: Faltering is detected in roughly
10 seconds, a full outage in roughly 25–30.

## Glitches

A **glitch** is trouble too small to move the state machine at all: a lost
packet or three, the kind of thing you experience as a page loading slowly
or a stream buffering. ConnectionWatcher records these separately rather
than pretending they're outages:

- An **episode** opens on the first failed probe from a Responding target
  and closes after five consecutive successes. If it contained **two or
  more** losses, it's recorded as a glitch (a single stray loss stays in
  the rollups only).
- If the episode escalates to any state transition (Faltering or beyond),
  it is *not* a glitch — the transition ledger owns it. Three tiers:
  glitch (no transition) → faltering episode (transitions) → outage
  (Down).
- Episodes on the same target within 60 s of each other coalesce into one
  glitch row, so a rough patch is one record, not twenty.
- When glitch episodes overlap on enough anchors at once (the same
  threshold the verdict uses for internet weather), one additional
  **internet glitch** is recorded naming the affected anchors — and noting
  whether a trunk target glitched too, which points the finger at your
  link rather than the wider internet.

Glitches are deliberately loss-based only — latency excursions don't count.
They never fire notifications; they exist for the History page's recurrence
punchcard and the ISP report, where months of nightly 3 AM misbehavior
show up as a vertical stripe.

## The verdict

The verdict is the banner at the top of the dashboard, the tray icon's
color, and the state that MQTT and webhooks broadcast. It is decided by
**end-to-end evidence**:

> Neither a ping reply nor a TCP connection proves a node forwards packets.
> The only proof that packets are forwarded is that they reach the next
> thing on the path.

So the **anchors decide reachability**, and the path hops only say *where*
the problem is once the anchors agree there is one. A Down path hop while
the anchors are answering never declares the internet down — it raises a
[model warning](#model-warnings-connectionwatcher-notes) instead, because
it means ConnectionWatcher's map of your network disagrees with reality
(wrong probe, redundant route, cellular failover…), and that's something
the administrator should know about without anyone getting paged.

### Counting anchors

- An anchor is **answering** when its state is Responding, Faltering, or
  Recovering — all of which mean it got replies in its window. Down is not
  answering; Starting and Initializing are *undetermined*.
- **One answering anchor outside your ISP proves the internet is up.** An
  anchor tagged with your ISP's operator name is **on-net**: it can stay
  reachable while your ISP has lost the rest of the internet, so it never
  proves "up" (it still counts toward classifying weather). Your ISP is
  identified by the operator tags on your path hops; if no path hop carries
  a tag, no anchor is assumed to be the ISP's and every anchor counts.
- If *every* anchor you configured belongs to your ISP, they're used as a
  weaker fallback and a standing note (`UnprovableUp`) tells you to add an
  anchor outside your ISP or tag your path.
- **Operator counting.** For classifying how *widespread* a problem is,
  anchors are counted by operator: four addresses of one provider failing
  together is one site having a bad day, never internet weather. The number
  of distinct degraded operators that turns a SiteIncident into
  InternetWeather is the `Verdict.WeatherThreshold` setting (default 2, on
  the Pools page).

### The verdict states

| Anchor evidence | Verdict |
|---|---|
| No anchor has reported yet | **Unknown** ("No anchors determined yet") |
| At least one (non-ISP) anchor answering, none degraded | **Healthy** |
| Answering, some anchors degraded, fewer than the weather threshold | **SiteIncident** — some site is having a bad day; not your problem |
| Answering, enough distinct operators degraded, loss attributable to your own path | **LinkDegraded** — your connection is impaired, blamed on the deepest path hop carrying real forwarding loss |
| Answering, enough distinct operators degraded, no path attribution | **InternetWeather** — widespread trouble out there; also not your problem |
| Anchors answering but every system resolver is dead | **DnsDown** — "UP, but name lookups are failing" (see below) |
| No anchor answering | **LinkDown** — blamed on a path hop (see below) |

When no anchor is answering but an undetermined one could still report,
the verdict waits — briefly (30 s, the `AnchorWaitCap`) — before declaring
LinkDown, so one permanently dead anchor reporting first at startup can't
blip the banner. A partial answer never waits: one answering anchor already
proves the conclusion.

### Blame: last good hop, first bad hop

When the anchors say down, ConnectionWatcher walks each anchor's path
outward from your router:

- A hop has **proven forwarding** if it — or anything beyond it on that
  path — is answering.
- The **frontier** is the farthest hop with proven forwarding; the blamed
  hop is the first non-answering hop after it. If every path hop answers
  and only the anchors fail, the blame is "beyond your last hop," named
  with the last hop's operator.
- A silent hop *before* a hop with proven forwarding is skipped for blame
  — traffic is visibly getting past it — and raises a model warning
  instead.

That's how the banner can say "Comcast is not carrying your traffic"
instead of just "something's wrong": the operator tag of the blamed hop
supplies the name.

### Loss attribution and the probe-only veto

Real forwarding loss at a hop shows up at everything beyond it, so a hop's
*forwarding loss* can't exceed the lowest loss seen beyond it. Loss above
that figure belongs to the hop's own probe — a router rate-limiting pings
to itself, a busy control plane — not to the path.

ConnectionWatcher computes this from burst-aligned windows (samples from
the same sweep, the same time span). It is used in exactly two places:

- To choose **LinkDegraded vs InternetWeather** when the anchors are
  degraded: the loss is yours when a path hop is carrying real forwarding
  loss.
- To raise the **ProbeOnlyLoss** note on a hop that habitually drops its
  own pings while forwarding fine ("common router behavior; consider a TCP
  probe") — and while that note holds, the verdict refuses to blame that
  hop. That veto is what keeps a ping-deprioritizing router from turning
  internet weather into a false "your link is degraded."

Loss attribution never changes any target's own state. It annotates and
blames; the state machines run on their own evidence.

## Unreachable and root-cause grouping

Older versions parked everything behind a Down hop as "unreachable" and
stopped probing it — which destroyed exactly the evidence that could prove
the hop was lying. Now:

- **Every target keeps probing on its own schedule, no matter what its
  ancestors are doing.** A target's state comes only from its own probes.
- **Unreachable is two observed facts, not an inference:** the target's own
  probes show it Down, *and* an ancestor is Down without proven
  forwarding. The ancestor recorded as the cause is the shallowest such
  hop — the **root cause**, which is also the hop the verdict blames.
- One router failure is **one incident**: everything that notifies or
  counts outages treats an explained target's transitions as part of its
  root cause's incident. Webhook payloads name the root cause
  (`ancestorId`) and by default skip explained transitions entirely; the
  tray, MQTT, the ISP report, and
  glitch correlation all group the same way. One real failure produces one
  alarm, not one per downstream target.
- **Explained time carries no data and no blame.** Samples taken while a
  target is Unreachable are not rolled up, and History counts the time as
  "unreachable — no data," never as the target's own outage. Your NAS
  shouldn't lose uptime points because the router rebooted.
- When the root cause recovers, each explained target is **released** to
  Starting and burst-probed: it re-proves its state on fresh evidence
  within seconds. A target that is genuinely still down comes back as Down
  — now its own problem.

## Model warnings (ConnectionWatcher notes)

A **note** says ConnectionWatcher's map of your network disagrees with what
the probes prove. Notes appear as a small wrench badge on the dashboard
(and a subtle dot on the tray icon) — never as a verdict change, a toast,
a webhook, or an MQTT state. "The administrator needs to know; nobody else
should be bothered."

| Note | Meaning |
|---|---|
| `HopBypassed` | A path hop is Down/Faltering while something beyond it answers — traffic is passing it. Its probe may measure a service rather than forwarding, it may not really be on the path, or your network has another route. |
| `ProbeOnlyLoss` | A hop drops some of its own pings but forwards fine. Consider a TCP probe. |
| `NeverAnswered` | A path hop has never answered since it was added, while things beyond it answer. Change its probe or remove it. |
| `UnprovableUp` | Every anchor belongs to your ISP, so "up" can't be proven. Add an outside anchor or tag your path. |
| `ResolverPartial` / `ResolverLying` / `ResolverFiltered` / `UpstreamResolverDown` / `NameUnresolvable` | Name-resolution oddities — see below. |

A note is active while its condition holds and clears itself when it
doesn't; every start and end is recorded so History can show it later.
Acknowledging a note hides it until it clears and recurs — the way to
silence a note for good is to fix the model it's complaining about.

## The name-resolution watch

"Is the internet down?" often has the answer "sort of": anything addressed
by IP works, anything addressed by name doesn't. ConnectionWatcher watches
for that directly:

- The resolvers your machine is configured with are discovered
  automatically (and tracked as the OS list changes) and probed with
  **real DNS queries** — one question per cycle, rotating through a short
  list of lookup names. The seeded names are the four anchor operators'
  own names (`dns.google`, `one.one.one.one`, `dns.quad9.net`,
  `dns.opendns.com`), chosen because their answer sets are published and
  stable; the hostnames of your hostname targets are asked too, and the
  list is editable.
- You can also name the **public resolver you use** (the setup wizard
  asks). It is queried directly, which lets the blame line distinguish
  *your resolver is down* from *DNS is down everywhere*.
- **DnsDown** fires only when the anchors prove the internet reachable but
  **every** system resolver is Down — the OS fails over between its
  configured resolvers, so your DNS works if any of them answers. The
  banner reads "Your internet is UP, but name lookups are failing," with
  the blame underneath. When the link itself is down, DNS failing says
  nothing new and is folded into the LinkDown detail.
- Because the seeded names have known answers, the watch can tell a
  **filtering** resolver from a broken one: a Pi-hole answering
  `dns.google` with `0.0.0.0` is a working resolver that blocks (the
  `ResolverFiltered` note suggests adding a lookup name it allows), while
  a resolver answering with some other address entirely gets the
  `ResolverLying` note — a captive portal or interception.
- Resolver targets live outside the reachability tree on purpose: the
  household resolver is usually the router's own address, and a DNS answer
  from it must never count as proof that the router forwards packets.

## Critical targets

Normally a single device being down is not news while the internet is fine
— that's the whole point of the verdict. But some devices you want to hear
about regardless. Mark a target **Critical** and its Down (and recovery)
becomes a reportable event even while the verdict is Healthy: a tray toast
("Router is not answering — internet still up"), a webhook even in
verdict-only mode, and a prominent state chip on the dashboard. The setup
wizard marks your router critical by default. Critical targets still never
change the verdict.

## Warm restart

Restarts are warm: after an update, reboot, or config change,
ConnectionWatcher seeds each target's last known state from the database
(or from the outgoing engine, on a config change) instead of re-measuring
the world from scratch. Seeded states are shown dimmed as *assumed* and
burst-confirmed by fresh pings within seconds; if the fresh sample agrees,
**no transition is recorded and the timeline is continuous across the
restart**. If it disagrees, one honest transition is recorded from the
seeded state. Database seeding applies only when the watcher was down
briefly (`Engine.WarmRestartMaxGapMinutes`, default 10 minutes; 0 disables;
not exposed in the UI) — after a long gap it cold-starts rather than
assuming the world stood still.

## What gets recorded

Three kinds of rows, and nothing else — raw pings are never stored:

- **Transitions** — every state change, with its structured reason and a
  stats snapshot. The authoritative incident record.
- **Rollups** — per-minute success rate and RTT min/avg/max per target,
  downsampled as they age (retention settings on the Data page control how
  long each granularity is kept).
- **Glitches** — the sub-transition events described above, pruned on the
  same schedule as fine-grained rollups.

History-page semantics worth knowing:

- **Outages vs no-data.** A target's stats separate *outages* (its own
  Down time) from *unreachable — no data* (time it was explained by
  something upstream). The two are different claims and History refuses to
  conflate them.
- **Address edits are three different things.** Correcting a bad address
  or repointing at a different device breaks the data history (stats are
  computed only after the break, with the older era still drawn, dimmed);
  a renumber (same interface, new address) is one continuous series. Only
  you know which, so the edit dialog asks.
- **Timestamps** are stored in UTC and rendered in your browser's local
  time. API query strings take ISO-8601; an explicit offset is honored and
  no offset means UTC.
