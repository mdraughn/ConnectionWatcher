# Incident Display Redesign — Design Notes

**Date:** 2026-09-10
**Status:** Directions chosen from mockups (canvas: "Incident Display Concepts",
https://claude.ai/code/artifact/d062c632-79de-4eb1-8061-2db930c0ed85).
Implementation plan deferred until the dual-WAN topology work settles (the
ribbon design includes twin WAN lanes).

## The problem being solved

The system computes the incident story — attribution, causality, "this is one
incident, not five" — and the current displays throw it away, showing
disaggregated per-target evidence the user must reassemble (e.g. Cloudflare's
loss spike on Sep 9 *looked* like Cloudflare's problem when the verdict layer
had already attributed it to the Router).

**Framing (Mark):** the dashboard tells the STORY; History keeps the
disaggregated EVIDENCE.

## Chosen displays (three, sharing one encoding)

1. **Cause-aligned ribbons** ("Ribbons v2") — History. Every target on one
   shared time axis; causality reads as vertical alignment. Includes twin
   touching External Router lanes (a/b) for the coming dual-WAN setup.
2. **Punchcard month** ("Punchcard v2") — dashboard. 30 days × 24 hours,
   empty paper = quiet; margin totals surface recurrence (the 3 AM stripe).
3. **Incident ledger** — dashboard companion / click-through target. Each
   incident as one plain-English sentence (generated from the attribution the
   verdict layer already computes) with a word-sized graphic. **Ledger icons
   use the same encoding below.**

Not chosen: the "story strip" lane view (superseded by punchcard + ledger on
the dashboard); original single-hue heatmap (superseded by Punchcard v2).

## The shared encoding (Mark's design)

Three independent visual variables:

| Variable | Encodes | Values |
| --- | --- | --- |
| **Shape** | Scope | Bar = shared-path trouble (internet-wide, or trunk); centered dot = one target's private problem |
| **Hue family** | Whose gear | Warm = out there (internet / a site); grey = your equipment (trunk) |
| **Depth** | Severity | Pale = glitch, mid = degraded (Faltering/Weather), deep = fully out (Down/LinkDown) |

**Palette** (validated: warm-vs-grey pairs pass CVD + normal-vision separation
at every tier; greys are intentionally achromatic):

- Warm ramp: `#eda94f` (glitch) → `#e08a00` (degraded) → `#cc3333` (out)
- Grey ramp: `#d4d4d4` (glitch) → `#7d7d7d` (degraded) → `#2b2b2b` (out)

**Special marks:**
- **Ambiguous trunk-vs-target**: half grey / half warm, split on the
  ascending diagonal (`linear-gradient(to bottom right, grey 49.5%, warm 50.5%)`).
- **Unreachable behind a down parent** (ribbons): light hatching
  (`repeating-linear-gradient(45deg, #c9c9c9 0 3px, #f2f2f2 3px 6px)`) —
  inherited, no data, no blame.
- Healthy ribbon baseline: `#eaf3ec`; quiet punchcard cells: blank paper.

**Ledger icons**: the leading chip on each ledger line is the same mark the
punchcard would draw for that incident (shape + hue family + depth), so the
three displays cross-reference by eye.

**Caveats recorded from the mockup pass:**
- At punchcard cell sizes the dot-vs-bar distinction needs the legend on
  first read; the legend is the trouble-type × severity matrix itself.
- `#e08a00` is below 3:1 contrast on white — every display keeps direct
  labels/tooltips as the relief (validator WARN, addressed structurally).
- The dashboard punchcard proposal is 30 days regardless of the History
  range selector.

## Open items before planning

- **Dual-WAN twin lanes**: display drawn (touching a/b lanes); detection and
  topology semantics under design — see the dual-WAN discussion (failover
  parent groups, active-path detection by RTT asymmetry).
- Placement confirmation: punchcard + ledger on dashboard, ribbons on
  History (working assumption per Mark's story/evidence split).
- New color vocabulary (warm/grey ramps) partially replaces the
  green/orange/red/gray status tokens in these three displays only; the tree,
  dots, and verdict banner keep today's colors.
