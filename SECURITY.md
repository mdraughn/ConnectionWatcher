# Security

## Reporting a vulnerability

Please report suspected vulnerabilities privately via
[GitHub security advisories](../../security/advisories/new) rather than a
public issue. You should get a human reply within a week. There is no bug
bounty — this is an MIT-licensed home project — but reports are genuinely
appreciated and fixes are prioritized.

## Security posture (read this before deploying)

ConnectionWatcher is designed for a **trusted home LAN**, and its trust
model is deliberately simple:

- **The watching surface is open by design.** Status, history, reports,
  exports (sanitized), `/metrics`, and `/mcp` serve without authentication —
  they expose what your dashboard shows. Anyone who can reach the port can
  see your connection history and target names.
- **The admin surface has a single-password gate.** Setting an admin
  password locks configuration, admin actions, traceroute, and setup behind
  a session cookie. It stops casual tinkering; it is not a hardened
  authentication system. There is no user management, no rate limiting on
  the login, and password recovery is "delete `admin-password.txt` next to
  the database".
- **Secrets stay put.** MQTT credentials, webhook URLs, and the heartbeat
  URL never leave the node through open endpoints — sanitized exports and
  snapshots exclude them, and logs elide secret URL paths. The one channel
  that transfers them, clone/take-over, requires the admin password and
  refuses to send them over plain HTTP to a non-local address unless you
  explicitly override.
- **No telemetry.** See the Privacy section of the README for the complete
  list of network traffic the app can generate.

If you expose ConnectionWatcher beyond your LAN, put it behind an
HTTPS-terminating reverse proxy and treat the open watching surface as
public information. Sub-path mounting is unsupported; give it its own
hostname.

## Supported versions

Only the latest release receives fixes. Container users: pull
`ghcr.io/mdraughn/conwatch:latest`.
