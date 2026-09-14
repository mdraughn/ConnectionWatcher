# Web UI redesign — artboards

Static mockups for the web UI redesign. The design rationale is in
[`../2026-09-14-web-ui-redesign-design.md`](../2026-09-14-web-ui-redesign-design.md).

Nothing here is wired into the build. These are design documents: self-contained
HTML that no part of ConWatch imports, serves, or compiles.

## The artboards

| File | Frame | What it shows |
|---|---|---|
| `Main.dc.html` | 1440×1360 | Dashboard, light, `SiteIncident` verdict |
| `DashboardDark.dc.html` | 1440×1360 | Dashboard, dark, `LinkDown` mid-outage |
| `History.dc.html` | 1440×2260 | History, dark — stats, ribbon, charts, punchcard, ribbon board, ledger |
| `Targets.dc.html` | 1440×1540 | Targets, light — sub-nav, pipeline stepper, table, add form |
| `Mobile.dc.html` | 390×1660 | Dashboard at phone width, full scrolling page |
| `MobileNav.dc.html` | 390×844 | Phone nav drawer open over a dimmed page |
| `System.dc.html` | 1440×1460 | Design language — tokens, ramps, verdict states, icons, type, controls |

Page 2 of the canvas, **Verdict icons** — four competing directions for the
major/minor verdict icon, plus the rules they share:

| File | Frame | What it shows |
|---|---|---|
| `IconSignal.dc.html` | 720×1010 | A · Signal — the brand mark carrying state |
| `IconPath.dc.html` | 720×1010 | B · Path — you → your gear → the world |
| `IconGlobe.dc.html` | 720×1010 | C · Globe — the conventional read |
| `IconWeather.dc.html` | 720×1010 | D · Weather — sun / cloud / rain |
| `IconSystem.dc.html` | 1440×1180 | Composition rules, the shared minor badge, app-icon sizes |

Each direction board carries the same four tests: the three major states large,
a size ladder down to 16 px **with a greyscale row**, the two minor badges
composed onto the glyph, and the glyph in place (banner, nav pip, browser tab).

Light and dark are split across the screens set deliberately so both themes are exercised
on chart-heavy and form-heavy pages, not just on the dashboard. `System.dc.html`
shows both side by side for direct comparison.

## Regenerating

```bash
node build-all.mjs
```

Output is deterministic — the synthetic chart and punchcard data use a seeded LCG,
so a rebuild is byte-identical. Editing an artboard by hand works, but the next
`build-all` overwrites it; change the generator instead.

Layout:

- `tokens-light.txt` / `tokens-dark.txt` — the two token blocks, inlined onto each
  artboard's root element.
- `lib-shell.mjs` — app bar, page band, section rule, icon set, page wrapper.
- `lib-parts.mjs` — verdict card, event strip, tree, ledger, glitch table, status bar.
- `lib-history.mjs` — stat tiles, state ribbon, latency line, loss bars, punchcard,
  ribbon board, encoding legend.
- `lib-icons.mjs` — the four verdict-icon metaphors and the shared minor badge.
- `build-*.mjs` — one per page group; `build-all.mjs` runs them in order.

## `canvas.json`

Frame positions, titles and sticky notes for the pan/zoom design canvas. The `w`/`h`
of each entry must match that file's root `width`/`min-height` or the frame clips.

## The published canvas

These artboards were published as an interactive design canvas (pan/zoom, per-element
editing, PNG/PDF export). That published page bundles ~2.8 MB of vendored editor code,
so it is **not** checked in — it is a packaging step, regenerable from these files by
re-running the `/design` skill.

Artboard `.dc.html` files use a small component format: a `<helmet>` block for
document-level CSS, everything else inline styles so the canvas editor can edit any
element directly.

**They open fine in a browser on their own** — double-click one, or
`start docs/design/web-ui/Main.dc.html`. The `./support.js` line in each `<head>` is
a placeholder the canvas swaps for its runtime; it 404s standalone and nothing is
lost, because these artboards are fully static — no template holes, no logic. The
unknown `<x-dc>` / `<helmet>` wrappers render their children, and the `<style>`
inside `<helmet>` still applies. Verified against `Main`, `DashboardDark` and
`MobileNav`.
