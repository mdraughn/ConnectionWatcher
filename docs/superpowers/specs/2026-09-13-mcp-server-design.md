# MCP server — design

**Date:** 2026-09-13
**Status:** APPROVED (backlog item; "Let's do prune-by-date + VACUUM and MCP").

## Goal

Claude answers "how was my internet last night?" from the actual database:
ConnectionWatcher exposes a Model Context Protocol endpoint so any MCP host
(Claude Code, Claude desktop) can query live status and history directly.

## Deployment model

**Embedded remote streamable-HTTP** at `/mcp` on the existing ConWatch server
(official `ModelContextProtocol.AspNetCore` C# SDK, stateless HTTP transport).
The app already is an always-on web server on the node; embedding means zero
extra install, one deployment, and it rides the same reverse proxy. Connect
with:

    claude mcp add --transport http conwatch http://nas.example.com:8080/mcp

## Auth posture

`/mcp` is OPEN, like the watching surface: every tool is read-only over data
the open APIs already serve (status, transitions, glitches, and the report,
whose page is open). No tool touches settings, secrets, config mutation, or
the locked surface, and none ever will without moving behind auth. The API
surface-lock test covers `/api/v1/*`; `/mcp` is documented here as a
deliberately open, read-only surface.

## Tools (Pattern A — one per action, all read-only)

1. **`get_current_status`** — the verdict (state + detail + blamed operator),
   every target's live state/success-rate/RTT, whether monitoring is enabled
   (archive mode), and the node's version/build time.
2. **`get_connection_report`** `(from_utc, to_utc)` — the ISP report as data:
   attributable episodes with recorded evidence, total down/degraded/weather
   time, anchor uptime, and the glitch summary. The headline tool for "how
   was my internet last night".
3. **`get_transitions`** `(from_utc?, to_utc?, subject_name?, limit=100)` —
   the raw state-transition log (newest first) for detailed digging.
4. **`get_glitches`** `(from_utc?, to_utc?, limit=100)` — sub-outage loss
   episodes, including internet-correlated ones.

All times are ISO-8601 UTC both directions; tool descriptions say so and tell
the model to convert for the user. Responses are compact JSON strings.

## Testing

Integration test drives the real endpoint in-memory: MCP client (or raw
JSON-RPC) initialize → tools/list (4 tools, descriptions present) →
tools/call for status and report against a seeded instance.

## Non-goals

- No traceroute tool (that surface is admin-locked; revisit if wanted).
- No write tools, prompts, resources, or elicitation in v1.
- No Anthropic Directory submission — this is a self-hosted node's endpoint.
