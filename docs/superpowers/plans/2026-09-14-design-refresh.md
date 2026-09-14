# Design refresh — implementation plan

Design: ../../design/2026-09-14-web-ui-redesign-design.md (drawn BEFORE the
verdict-slab redesign; per Mark, our dashboard changes override it).

## Review verdict

Workable and faithful to the app's own language. Implementing on branch
`design-refresh` (NOT merged — Mark reviews it live first), with these
resolutions where the design and our later decisions collide:

1. **§6.1 tinted verdict card → OVERRIDDEN, flagged.** The design de-emphasizes
   the slab; our 2026-09-13 redesign made the slab THE at-a-glance answer
   ("is my internet up?"). Keeping the full-bleed colored slab with the
   UP/STRUGGLING/DOWN copy, restyled to the new chrome (radius, type, shadow).
   The tinted-card treatment remains a one-rule swap if Mark prefers it after
   seeing both.
2. **§6.2 SiteIncident amber → OVERRIDDEN.** Decided 2026-09-13: only YOUR
   line's trouble changes the slab color; a site incident stays green with a
   warm-marked caveat. The design itself marked this "needs confirming".
3. **Verdict pip → ADAPTED to the slab's answer language.** The pip (bar-borne
   verdict on every page) is a great extension of the up-at-a-glance goal, but
   it shows OUR answer, not the taxonomy: green "Up", amber "Struggling",
   red "Down", grey "Checking" / "Paused" (archive mode).

Adopted as designed: token system + dark mode (incl. the grey-ramp inversion —
encoding colors move to CSS variables so dark can re-point them), 56px app bar
with grouped nav/icons/active underline, version chip replacing the under-brand
stamp, hamburger drawer <900px, page bands, section rules, mono for numbers,
tree row grid with address column (desktop only; §7 flags it droppable),
History stat tiles + segmented range, Targets segmented sub-nav,
prefers-reduced-motion for the expandable transition.

Deferred (per design §5, "not drawn"): page-specific design for Data /
Traceroute / Admin / Setup / Report beyond the inherited shell; full pipeline
stepper redesign (polish only); chart single-series fine-art beyond styling.

## Order

1. Tokens (light/dark) + theme switcher (system default, localStorage
   override, pre-paint inline script) in site.css/_Layout.
2. encoding.ts → CSS variables (dark grey-ramp inversion); Vitest updates.
3. App bar + drawer + page bands + section-rule styling; pip fed by a small
   shell poller of /api/v1/status.
4. Dashboard: slab restyle, tree row grid; History: tiles + segmented range;
   Targets sub-nav; mobile accommodations; reduced-motion.
5. Suites, preview on the branch for review. NO merge.
