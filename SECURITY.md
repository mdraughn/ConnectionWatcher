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
  exports (sanitized), `/metrics`, and `/mcp` (except its `list_discoveries` tool, below) serve
  without authentication —
  they expose what your dashboard shows. Anyone who can reach the port can
  see your connection history and target names. That includes any LAN
  devices you track as Local targets: their names, addresses and up/down
  history appear across the open surface — for example `/api/v1/status`,
  the transition and glitch history endpoints, the sanitized snapshot
  export, `/metrics`, the dashboard and History pages, and the MCP
  tools — so treat that list like a network inventory when deciding
  who can reach the port. The Discoveries inbox (every router, network-edge
  and local-device candidate a search or a tray scout found, with hostnames
  and the scout's machine name) follows the admin gate: its page, its REST
  API and the MCP `list_discoveries` tool are open only while no admin
  password is set.
- **The admin surface has a single-password gate.** Setting an admin
  password locks configuration, settings and admin actions, the Data page,
  traceroute, and setup behind a session cookie. It stops casual tinkering; it is not a hardened
  authentication system. There is no user management, no rate limiting on
  the login, and password recovery is "delete `admin-password.txt` next to
  the database".
- **Secrets stay put.** MQTT credentials, webhook URLs, and the heartbeat
  URL never leave the node through open endpoints — sanitized exports and
  snapshots exclude them, and logs elide secret URL paths. The one channel
  that transfers them, clone/take-over, requires the admin password and
  refuses to send them over plain HTTP to a non-local address unless you
  explicitly override. On disk, the Windows installer (`conwatch service
  install`) restricts the data folder — database, admin password, session
  keys — to SYSTEM and Administrators; other local users can read only the
  config file.
- **The tray remembers an admin password only if you ask it to.** When the Windows tray scouts
  for a server that has an admin password, ticking Remember saves that password encrypted with
  DPAPI for your Windows account in `scout.json` under `%APPDATA%\DSoftCo\ConnectionWatcher`. It is
  bound to the server URL it was entered for, saved only after the server accepts it, and never sent
  over plain HTTP to another machine unless you approve that host (an approval that lasts only for
  the session). "Forget saved admin password" in the tray settings deletes it.
- **No telemetry.** See [PRIVACY.md](PRIVACY.md) for the complete list of
  network traffic the app can generate.

If you expose ConnectionWatcher beyond your LAN, put it behind an
HTTPS-terminating reverse proxy and treat the open watching surface as
public information. Sub-path mounting is unsupported; give it its own
hostname.

## Supported versions

Only the latest release receives fixes. Container users: pull
`ghcr.io/mdraughn/conwatch:latest`.
