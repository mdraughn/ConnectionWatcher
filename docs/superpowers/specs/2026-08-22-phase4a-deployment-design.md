# ConnectionWatcher Phase 4a — Deployment (Service, Tray, Container)

**Date:** 2026-08-22
**Status:** Approved (scope settled in conversation; MQTT/HA and on-NAS
verification explicitly postponed — they need user-side setup time).

## Scope

| In | Out (postponed) |
|---|---|
| Windows Service support in ConWatch | MQTT/webhooks/Home Assistant |
| Install/uninstall PowerShell scripts (port probe-and-pin, firewall rule, sc recovery, Start Menu shortcut, open-once) | Toast notifications (decide alongside MQTT) |
| ConTray: verdict-colored tray companion (new Windows-only project) | Synology deployment verification (Dockerfile ships locally tested, marked "not yet verified on NAS") |
| Dockerfile + compose example, built and smoke-tested locally | mDNS discovery |

## Decisions (from conversation)

- **Port**: installer probes once (default 8080, walks candidates, `-Port`
  override), pins it in the service definition, creates the matching firewall
  rule, prints the final URL. Runtime bind failure logs a clear port-in-use
  message naming the port instead of crash-looping mutely.
- **Discoverability**: Start Menu shortcut + open dashboard at install end;
  tray companion as its own user-session app (services cannot own tray icons):
  green = Healthy/SiteIncident, orange = Weather/LinkDegraded, red = LinkDown,
  gray = service unreachable; no-verdict fallback derives color from target
  states. Left-click opens dashboard; menu: Dashboard/History/Report/Admin,
  Exit. Auto-start via HKCU Run key (installer-managed).
- **Service mechanics**: `AddWindowsService` + content root pinned to the exe
  directory when running as a service (data/ lands next to the app); listen
  URL passed via service arguments; recovery = restart on failure escalating
  delays; scripts require elevation and are idempotent (re-run = reconfigure).
- **Container**: multi-stage Dockerfile (SDK+Node build → aspnet runtime),
  `data/` volume, port 8080 internal, HEALTHCHECK on `/api/v1/health`;
  compose example documents `cap_add: NET_RAW` for non-root use (root default
  works without it). Built and run locally (Docker Desktop): health + real
  ICMP verified in-container. Synology steps documented but unverified.

## Deliverables

1. `Programs/ConWatch` service enablement (no behavior change run normally).
2. `deploy/windows/install-service.ps1`, `uninstall-service.ps1`.
3. `Programs/ConTray` (net10.0-windows WinForms, NotifyIcon; reads URL from
   `contray.json` beside the exe, written by the installer).
4. `Dockerfile`, `.dockerignore`, `deploy/docker/compose.yaml`, docs.
5. README deployment section.

## Testing

Existing suites stay green (service wiring must not change console behavior —
integration tests are the guard). Install scripts: syntax-validated + local
run where elevation-free parts allow. ConTray: pure status→color mapping kept
in one reviewed class; run-smoke locally. Container: `docker build` + run +
health + add-target + Responding check locally.
