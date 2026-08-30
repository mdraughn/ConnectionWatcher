# Admin Password — Design

**Date:** 2026-08-30
**Status:** Approved (this doc reflects the interview + design approved in chat)

## Purpose

A single site password that prevents casual tinkering with ConnectionWatcher's
configuration. It gates *changing* things; watching is always open. It is
explicitly **not** internet-grade security: the password travels over plain
HTTP on the LAN, and the reset path is filesystem access. The UI says so.

## Decisions (from interview)

| Question | Decision |
| --- | --- |
| Lock scope when not in admin mode | Protected pages hidden entirely: nav links gone, direct visits redirect to the dashboard password prompt, their APIs return 401 |
| Admin-mode lifetime | Session cookie — survives navigation, ends when the browser closes; explicit "exit admin mode" link ends it sooner |
| Exports + ISP report | Both stay open (read-only); only state-changing surfaces are locked |
| Password storage | `admin-password.txt` beside the database in the data directory; salted PBKDF2 hash, never plaintext |
| No password set | Site fully open (today's behavior); Admin page shows a "Set admin password" card |
| Forgot password | Message shows the resolved file path: delete the file to remove the password; takes effect on next request, no restart |
| Set-time warning | When setting (or changing) the password, the admin sees a warning: this prevents casual tinkering but is not internet-grade security |

## What is locked (gate on = password file exists, visitor not in admin mode)

**Pages** — hidden from nav; direct visit redirects to `/` with the password
prompt open:
- `/Config/Targets`, `/Config/Profiles`, `/Config/Pools`
- `/Admin`
- `/Traceroute`
- `/Setup` (the wizard edits config)

**APIs** — return 401:
- All config CRUD (targets, profiles, pools) including whole-config
  export/import (the JSON config export contains addresses and lives on the
  hidden Config page)
- `GET` and `PUT /api/v1/settings` (the GET response includes the MQTT
  broker password; only the hidden Admin page consumes it)
- All `/api/v1/admin/*` (stats, mqtt-test, clear-history, factory-reset)
- Setup/wizard endpoints
- Traceroute endpoints

**Always open** (no password ever asked):
- Dashboard `/`, `/History`, `/Report`
- `GET /api/v1/status`, history/rollup queries,
  `GET /api/v1/config/health`, `/api/v1/export/*` (measurement-data
  exports: transitions/rollups), `/api/v1/health`
- SignalR hub `/hubs/status`

## Components

### AdminGate (ConWatch service, singleton)

Owns the password file. Path: `Path.Combine(dataDir, "admin-password.txt")`
where `dataDir` is the directory of the configured `Persistence:DbPath`.

- `bool IsEnabled` — file exists (checked per call with a short mtime-based
  cache; deletion is picked up on the next request without restart).
- `bool Verify(string password)` — PBKDF2 compare; constant-time equality.
- `void Set(string password)` — writes `pbkdf2$<iterations>$<saltB64>$<hashB64>`.
- `void Remove()` — deletes the file.
- `string FilePath` — for the forgot-password message.

File format is one line, self-describing, hand-deletable. 100k iterations,
SHA-256, 16-byte salt.

### Auth wiring

- Cookie authentication, single hardcoded identity ("admin"), no users table.
- Session cookie (no persistent expiry); sliding not required.
- Data Protection keys persisted to the data directory so cookies survive a
  service restart mid-browser-session.
- `POST /api/v1/auth/login { password }` → 204 + cookie, or 401 after a fixed
  1-second delay (brute-force friction). When `IsEnabled` is false, login is
  pointless and returns 400.
- `POST /api/v1/auth/logout` → clears cookie.
- `GET /api/v1/auth/status` → `{ enabled, admin }` for UI rendering.

### Enforcement

- An endpoint filter (`RequireAdmin`) applied to the protected API groups:
  passes when `!AdminGate.IsEnabled` or the caller has the admin cookie;
  otherwise 401 `{ errors: ["Admin password required."] }`.
- Protected Razor pages check the same condition in `OnGet` and redirect to
  `/?admin=1` (dashboard with the prompt open).
- Nav renders protected links only when `!IsEnabled || admin`.

### UI

- **Dashboard**: small lock button (visible only when `enabled && !admin`)
  opens an inline password dialog. Wrong password → error line. A "Forgot?"
  link reveals the reset message with the real file path. `/?admin=1` opens
  the dialog on load (used by page redirects).
- **Nav (admin mode, gate on)**: "exit admin mode" link → logout → reload.
- **Admin page**:
  - Gate off: "Set admin password" card (password + confirm) with the
    warning: *"This prevents casual tinkering with your configuration. It is
    not internet-grade security — the password is sent over plain HTTP on
    your network, and anyone with access to the server's files can remove
    it."* Setting the password immediately enters admin mode (sets cookie)
    so the admin isn't locked out of the page they're standing on.
  - Gate on (admin mode): "Change password" (current + new + confirm, same
    warning) and "Remove password" (current required) cards.
- `POST /api/v1/auth/set-password { current?, password }` — current required
  iff already enabled; min length 4; sets cookie on success.
- `POST /api/v1/auth/remove-password { current }`.

### Interactions with existing behavior

- Fresh install: no file → wizard and everything else work exactly as today.
- Tray: Dashboard/History/Report links unchanged; the Admin link lands on
  the password prompt when the gate is on.
- Factory reset does NOT delete the password file (resetting config shouldn't
  drop the lock); removing protection is its own explicit action (UI card or
  file deletion).
- The mid-rebuild 503 behavior, SignalR, and status APIs are untouched.

## Error handling

- Password file unreadable/corrupt → treat as enabled-but-unverifiable: log a
  warning, `Verify` returns false, forgot-password path (delete the file)
  still works. Never fail open on a corrupt file.
- File deleted while sessions active: gate reads `IsEnabled` per request, so
  everything opens; stale cookies are harmless.

## Testing

Integration tests through `ConWatchFactory` (temp data dir per test):

1. No password file → protected pages 200, mutating APIs work (today's suite
   already covers much of this; add explicit auth-status check).
2. Set password → protected APIs 401 without cookie, pages redirect; open
   surfaces still 200.
3. Login wrong password → 401 (and the delay is present); right password →
   cookie → protected APIs work.
4. Logout → 401 again.
5. Delete the file → everything open on next request, no restart.
6. Change password: old stops working, new works; remove-password requires
   current.
7. Set-password response enters admin mode immediately.
8. AdminGate unit tests: file format round-trip, corrupt file → not verified.

Unit tests for `AdminGate` live in ConWatch.Tests (the service is ConWatch-
local; nothing below ConWatch changes).

## Out of scope (YAGNI)

- Multiple users, roles, password recovery beyond file deletion, HTTPS,
  rate-limiting beyond the fixed delay, API tokens for scripts, lockouts.
