# ConnectionWatcher — Web UI Redesign

**Date:** 2026-09-14
**Status:** Draft design, awaiting review — nothing implemented
**Scope:** The presentation layer of the app's own web UI (`Programs/ConWatch/Pages`,
`wwwroot/css/site.css`, `ClientApp/src`). Page inventory, page order, and the
interaction model are **unchanged**; this is chrome, theming, responsiveness and
wayfinding only.

## 1. Purpose

The UI works and the arrangement of pages is right. What it lacks is a coherent
design language: a Pico default `<nav>` with no active-page marker, no responsive
collapse, a run of `h3`/`h4` doing the work of section structure, no dark mode, and
no iconography. The brief was: *more professional layout, more responsive (hamburger
menu), a nice dark mode, better iconography and wayfinding — without overwhelming a
useful tool with design.*

Explicit non-goals:

- No change to which pages exist, what is on them, or the order of sections.
- No change to the interaction model (click-to-pin, expand-in-place, SignalR live
  updates).
- No new features, no new data on screen except where noted in §7.

## 2. Foundations

Everything is lifted from what the app already uses — Pico 2's resolved values and
`ClientApp/src/lib/encoding.ts` — not invented.

### 2.1 Surfaces and ink

| Role | Light | Dark |
|---|---|---|
| page | `#ffffff` | `#13161f` |
| surface | `#fbfcfd` | `#181c26` |
| raised | `#f4f6f9` | `#1e2331` |
| well | `#eef1f5` | `#252b3a` |
| hairline | `#e7eaf0` | `#262d3b` |
| border | `#d6dbe4` | `#323a4b` |
| ink | `#373c44` | `#c2c7d0` |
| ink muted | `#646b79` | `#7b8495` |
| ink faint | `#8b93a1` | `#68707f` |
| accent | `#0172ad` | `#01aaff` |

Light `page`/`ink`/`muted`/`accent` and every dark value are Pico 2's own tokens.
The intermediate surfaces and the two faint inks are new, because Pico has no
elevation scale and the design needs cards on a ground.

### 2.2 Status ramp

Existing `--cw-*` values are kept unchanged in light. Dark gets its own steps —
the light reds and greens are too dark against `#13161f`:

| Token | Light | Dark |
|---|---|---|
| `--cw-ok` | `#2e9e52` | `#3fb567` |
| `--cw-warn` | `#e08a00` | `#f0a02a` |
| `--cw-down` | `#cc3333` | `#e8544b` |
| `--cw-unreach` | `#999999` | `#6d7585` |

### 2.3 Incident encoding

`shape = scope · hue = whose gear · depth = severity` is unchanged as a scheme.

| | Light | Dark |
|---|---|---|
| warm glitch / degraded / out | `#eda94f` `#e08a00` `#cc3333` | `#c98a3c` `#f0a02a` `#e8544b` |
| grey glitch / degraded / out | `#d4d4d4` `#7d7d7d` `#2b2b2b` | `#39404f` `#8c94a6` `#e2e6ee` |
| healthy (large surfaces) | `#eaf3ec` | `#1b2620` |
| healthy (small marks) | `#4fa96e` | `#46a86c` |

**The grey ramp inverts in dark, and this is the one substantive decision in the
whole redesign.** In light it runs pale → black. `#2b2b2b` is invisible on a
`#13161f` page, so a naive theme flip would erase "your equipment is fully out" —
the most severe mark the system can draw. In dark it runs dark → near-white
instead: *deeper = worse* still holds, it simply runs toward light. The warm ramp
keeps its direction in both themes, so warm-vs-grey separation survives at every
tier, which is what the colourblind validation in `encoding.ts` depends on.

### 2.4 Type

`system-ui` stack for text, exactly as Pico ships it. A tabular mono stack
(`ui-monospace, 'Cascadia Mono', 'Segoe UI Mono', Consolas, monospace`) is new, and
carries **every number**: RTT, success rate, timestamps, addresses, counts. The app
already reached for `font-variant-numeric: tabular-nums` on `.cw-row .stats`; this
makes it the rule.

> Implementation note: font stacks here are written with **single**-quoted family
> names on purpose. These artboards inline them into `style="…"` attributes, where a
> double quote terminates the attribute and silently discards everything after it.

### 2.5 Scale

Radii 4px (controls, chips) / 8px (cards) / 999px (pills). Control height 32px
desktop. Icons 16px in nav and inline, 20px grid, 1.6px stroke, round caps.

## 3. The shell

A 56px app bar replaces the Pico `<nav>`:

- Brand mark (concentric arcs — you at the centre, hops going out), wordmark, and
  the build version as a quiet mono chip. The absolutely-positioned `.cw-build`
  stamp under the brand goes away.
- Nav grouped **Watch** (Dashboard, History) | **Manage** (Data, Targets,
  Traceroute, Admin), split by a hairline. The grouping already exists implicitly —
  `AdminGate` hides exactly the second group.
- Each item carries a 16px icon; the active item is full-strength ink with a 2px
  accent underline.
- Right cluster: the **verdict pip**, theme toggle, admin-mode lock.

**The verdict pip is the main wayfinding change.** Today the verdict exists only on
the dashboard. Carrying a dot + label in the bar means History, Data and Targets all
answer "is it up right now?" without navigating.

Below the bar, every page opens with a **page band**: title plus one sentence saying
what the page is for, with page-level actions right-aligned. Sections are then named
by a rule — uppercase micro-label, hairline, optional right-hand note — replacing the
`h3`/`h4` run.

## 4. Responsive

| Width | Behaviour |
|---|---|
| ≥ 900px | Full bar. Content centred at 1250px, 26px gutters. |
| < 900px | Nav collapses to a hamburger drawer. Verdict pip **stays in the bar**. Gutters 16px, tree rows 44px, tables restack as cards. |

The drawer carries the same Watch/Manage grouping as labelled sections, plus a
Light/Dark/System control and the build stamp in its footer.

Specific phone accommodations: the tree drops the address column (it lives on
Targets), the glitch table becomes stacked cards, and the event strip is sized to
the container rather than to a fixed bin count.

## 5. Per-page notes

- **Dashboard** — order untouched: verdict → event strip → tree → ledger → glitches
  → status bar. The verdict and strip merge into one "right now" card. Tree rows
  become a proper row grid (dot, name, address, state chip, rate, RTT) instead of
  name plus a right-floated stats blob.
- **History** — controls become a styled subject select plus a segmented range
  control. Stats become four tiles. Charts follow single-series rules: 2px line, no
  legend (the section title names it), recessive grid, one direct label at the peak.
  Punchcard and ribbon board keep their existing geometry.
- **Targets** — `/Config/Targets`, `/Profiles`, `/Pools` become a segmented sub-nav
  rather than three separate concerns. The `cw-pipeline` becomes a proper stepper
  with node dots, ISP chips and an explicit "insert a hop here" affordance.
- **Data / Traceroute / Admin / Setup / Report** — not drawn. They inherit the shell
  and the section-rule pattern; no page-specific design was done.

## 6. Deliberate deviations from today's behaviour

1. **The verdict banner is a tinted card with an accent edge, not a full-bleed
   colour slab.** A saturated slab across the top of a healthy page every time it
   loads is loud for a tool that is usually reporting good news. Severity is carried
   by a 6px (rather than 4px) edge plus an alert glyph. **This is reversible in one
   rule if the slab is preferred.**
2. **`SiteIncident` is drawn amber (`--warm-glitch`), not green.** `site.css`
   currently groups `.verdict-banner.siteincident` with `.healthy` under `--cw-ok`,
   so a site incident announces itself in green. That looks more like an accident
   than an intention — **needs confirming before implementing.**

## 7. Open questions

- The desktop tree now shows each target's IP beside its name, where today it is
  only a `title` tooltip. This is the one place material was added rather than
  restyled. Drop it if it reads as noise.
- Whether the two deviations in §6 are wanted.
- Nothing has been done for `prefers-reduced-motion` against the existing
  `cw-expandable` grid-rows transition.

## 8. Artboards

See `web-ui/` — seven `.dc.html` artboards plus `canvas.json`, and the generator
that produces them. `web-ui/README.md` covers regeneration.

Sample data throughout (Comcast hops, the NAS, Home Assistant, four anchors) is
realistic but **invented**; it is not a capture of a real database.
