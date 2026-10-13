# Paired-Probe Veto (Plan D2) — Design Notes

**Date:** 2026-10-13
**Status:** **Parked** (Mark, 2026-10-13). This records a design discussion that was stopped before the pair source was chosen. It is not an approved spec.
**What ships instead:** the cheap veto. The verdict never blames a hop while its `ProbeOnlyLoss` note holds (see §6).
**Context:** spec `2026-10-09-evidence-based-verdict-design.md` §4.5–4.6; plan `2026-10-10-loss-attribution.md` and its 2026-10-12 revisions.

## 1. Why this exists

The verdict blames our own path (`LinkDegraded`) when the anchors are degraded and a path hop is Faltering. It blames the shallowest such hop.

Some routers deprioritise pings addressed to themselves. They can sit in Faltering with 20–40 % "loss" while forwarding traffic perfectly. If internet weather coincides with that, the state rule blames the router: a false amber tray and a toast.

A **veto** stops the verdict from blaming a hop whose loss is only its own probe. Two attempts in Plan D failed:

1. **"Forwarding loss ≥ the anchors' median loss", per window.**
   - Forwarding loss is capped by the lowest loss beyond the hop. The anchors are beyond it, so the rule only fires on ties.
   - Simulated real link loss was blamed in only 3–24 % of windows.
   - The verdict flipped 3.5–22× an hour, and every flip notifies.
2. **A 3-window pooled veto: forwarding loss below 5 % vetoes, lifted at 10 %.**
   - It vetoed healthy hops too (0 % forwarding loss). The onset of real loss read `InternetWeather` and then `LinkDegraded`, so two notifications per incident.
   - It flapped 3–9× an hour at 10–20 % real loss. The minimum over a few witnesses with about 18 samples each is too noisy.

## 2. Mark's framing (2026-10-12)

The question is whether the network fits the **monotone reply model**. Let p[n] be the probability that a ping to node n in the routing chain gets a reply. A ping to n+1 must pass through n, and a conforming node replies at least as reliably as it forwards. So **p[x] ≥ p[x+1]**.

The veto is a test of that hypothesis on each **adjacent edge** (a hop and the next monitored target beyond it):

- H0 (conforming, within a margin): p[x+1] − p[x] ≤ δ
- H1 (x's probe is misleading): p[x+1] − p[x] > δ

Veto blaming x when H0 is rejected. Mark's example was δ = 0.05 at significance 0.01. A difference of reply probabilities is the right effect measure: it is the "probe-only loss rate" the note reports.

## 3. Pairing (McNemar's insight)

Ping x and x+1 at (nearly) the same instant and look only at the pairs that disagree:

- (x lost, x+1 answered) counts *against* the model.
- (x answered, x+1 lost) counts *for* it.

Loss that hits both at once (weather, upstream congestion) cancels out. Per pair, let D = +1 for the first case, −1 for the second, and 0 when they agree. Then E[D] = p[x+1] − p[x].

Where a hop has several children, test each edge and pool them into one stream for the parent. Under H0 every edge satisfies E[D] ≤ δ, so the pooled stream does too. Pooling 3 anchors triples the pair rate.

### 3.1 Pair sources considered (undecided)

| | **A. Piggyback** | **C. Pair existing samples by time** |
|---|---|---|
| Mechanism | Each scheduled child ping also triggers an immediate ping to its monitored parent, in alternating order. The extra results go only to the detector, never to the parent's state machine or cadence. | Match each child sample to the parent's sample nearest in *send* time. |
| Engine work | C's work, plus a "probe once, now" call on the parent's controller, routing results separately, two pings in flight to one parent, and a politeness cap. About 1.5–2× C's probing work. | Send timestamps on `PingResult` (results are stamped at completion today, so a lost ping is stamped at its timeout) and a pairing buffer per hop. |
| Router (1 s) → path hop (5 s) | exact pairs, 12/min | ≤ 0.5 s apart, 12/min; about as good as A |
| Path hop (5 s) → anchors (10 s, staggered) | exact, 6/min per anchor, 18/min pooled | up to 2.5 s apart (average about 1.25 s), 18/min pooled |
| Shared loss cancels | fully | partly; bursts shorter than about 1–2 s don't cancel |
| Rough time to veto (25 % probe-only, δ = 5 %) | about 2–4 min | about 4–8 min |
| Extra traffic | path hop about 2.5×; router +20 %; anchors none; TCP-probed parents get extra connects | none |

The leaning at parking time was **C first**, with the detector reading an abstract stream of pairs, then measuring on the NAS and adding A as a second source only if needed.

Note: pairing was not the main fault of the failed vetoes. The main faults were selection (the minimum over witnesses) and too little evidence. Pairing mainly cancels shared bursts.

## 4. Sequential detection

We monitor indefinitely. A fixed-sample test (McNemar) repeated at α = 0.01 guarantees false alarms over time, so the detector must be sequential. Two candidates:

1. **Betting e-process** on the bounded paired difference D.
   - Wealth evolves as K ← K·(1 + λ(D − δ)), with the bet λ chosen from past data only. Veto when K ≥ 1/α.
   - It is anytime-valid (Ville's inequality). It assumes only that each pair's conditional mean satisfies H0, so it stays valid under bursty, correlated loss.
   - Reference: Waudby-Smith & Ramdas, "Estimating means of bounded random variables by betting" (JRSS-B).
2. **Two-sided CUSUM.**
   - One chart detects entering non-conformance (mean D rising from about 0 to δ₁, say 0.15). A second detects the return (mean falling back below about δ).
   - It is cheap and well understood, but it assumes independent samples, so its false-alarm guarantee is optimistic under bursts.
   - "E-detectors" (Shin, Ramdas & Rinaldo) keep CUSUM's shape without the independence assumption.

**Calibration:** budget false alarms as an **average run length** ("no more than one false veto per month per edge"), not a per-test α. Calibrate by simulation or analytically.

**Confirmation tests:** probably unnecessary with a calibrated threshold. Use them only if the simulated false-alarm rate is still too high (for example, the alarm must persist one more epoch).

**Output:** the detector's state (conforming or not, per hop) would drive **both** the verdict veto and the `ProbeOnlyLoss` note, so the two can never disagree. It would replace the cheap veto in §6.

## 5. Why it was parked

The remaining false alarm needs internet weather to coincide with a hop that is already Faltering because it deprioritises ping. The cost of a miss is an amber "degraded" instead of a green "weather".

The `ProbeOnlyLoss` note already says when the model assumption fails at a hop and how to fix it (change the probe). Fixing the model removes the cause.

So a new probing subsystem plus a sequential detector is a lot of work for a rare case. Revisit it if History on the NAS shows `LinkDegraded` episodes that coincide with a `ProbeOnlyLoss` note on the blamed hop, or that slip through the cheap veto (for example, in its first 10 minutes after a restart).

## 6. What ships instead (the cheap veto)

The verdict never blames a hop while that hop's `ProbeOnlyLoss` note holds. That note averages 10 windows (raise at probe-only ≥ 15 % with forwarding ≤ 2 %, clear below 10 % or above 5 %).

- It is stable.
- It never vetoes healthy hops.
- It cannot disagree with the banner.

The cost: no veto for about 10 minutes after a restart or config edit, because the windows are not carried across.
