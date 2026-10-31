# ConnectionWatcher

[![ci](https://github.com/mdraughn/ConnectionWatcher/actions/workflows/ci.yml/badge.svg)](https://github.com/mdraughn/ConnectionWatcher/actions/workflows/ci.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

A home internet connectivity monitor that answers the question *"is my internet down,
and what kind of down is it?"*

ConnectionWatcher monitors your path to the internet and periodically checks a
few key websites, giving it the ability to pretty accurately tell the
difference between problems out on the internet and problems with your
connection to it.

![The dashboard: verdict, uptime clock, and the path to the internet](docs/images/dashboard.png)

When your connection *is* the problem, ConnectionWatcher tells you which hop
broke, keeps the evidence (a printable ISP report with timestamps your
provider can correlate), and can page you through Home Assistant, MQTT, or
webhooks. When it isn't, it says so — a Netflix outage should never send you
to reboot your modem.

## Documentation

- **[Theory of operation](THEORY_OF_OPERATION.md)** — how the verdict is
  decided: anchors, the reachability tree, states, glitches, blame, and why
  a router that ignores pings doesn't count as an outage.
- **[Privacy](PRIVACY.md)** — no telemetry, and the complete list of network
  traffic the app can generate.
- **[Security](SECURITY.md)** — the trust model, what's open and what's
  behind the admin password, and how to report a vulnerability.
- **[Development](DEVELOPMENT.md)** — building and running from source,
  project layout, tests, releases.
- **[Contributing](CONTRIBUTING.md)** — bug reports and pull requests.
- **[Docker deployment notes](deploy/docker/README.md)** — compose details,
  bridge vs. host networking, Synology tips.

## Quick start (Windows)

Most people's always-on machine is a Windows PC, so that's the easy path:
download `ConnectionWatcher-Setup-<version>.exe` from the
[Releases page](https://github.com/mdraughn/ConnectionWatcher/releases) and
run it. (Got a NAS or a home lab instead? See
[Running in Docker](#running-in-docker).)

### About Microsoft's warnings

Windows is suspicious of programs it hasn't seen many times before, so
SmartScreen may interpose a blue **"Windows protected your PC"** screen.
The installer *is* code-signed — click **More info** and the dialog should
show **Publisher: Mark Draughn**; that signature means the file is exactly
what was built from this repository by the release pipeline. Click **Run
anyway** to proceed. (You can also verify before running: right-click the
downloaded file → Properties → **Digital Signatures** tab.) If SmartScreen
shows *"Unknown publisher"* instead, stop — that's not a release build.

The installer then asks for administrator rights (it registers a Windows
service), so expect one UAC prompt naming the same publisher.

### The install wizard

*(screenshot to come: the installer's "What to install" page —
docs/images/install-what-to-install.png)*

1. **License** (MIT) and install folder (default
   `C:\Program Files\DSoftCo\ConnectionWatcher`).
2. **What to install** — two checkboxes, both normally checked:
   - **Service**: the monitoring engine and dashboard, run as a Windows
     service. Uncheck it only if another machine (a NAS, say) already runs
     the service and this PC just needs the tray icon.
   - **Tray companion**: the connection-status icon in your tray, started
     at sign-in.
3. **Dashboard port** (if installing the service) — the TCP port the
   dashboard listens on; 8080 is the usual choice. The firewall is opened
   for it on private and domain networks (not public ones).
4. **ConnectionWatcher server** (if installing the tray) — where the
   service runs. On a normal install the prefilled `http://localhost:8080`
   is already right; on a tray-only install, enter the server's address
   (`http://nas:8080`). Not sure yet? Keep the default — the tray's
   Settings dialog can change it any time.
5. Install, and finish with **Open the ConnectionWatcher dashboard**
   checked.

The result: a `ConnectionWatcher` service that starts with Windows (and
restarts itself if it crashes), the tray icon at sign-in, a Start Menu
"ConnectionWatcher Dashboard" link, and your browser open on the setup
wizard. Uninstalling removes the program, service, and tray, then asks —
twice — before touching your data.

### First-run setup

With an empty configuration, the dashboard redirects to the **quick-setup
wizard**:

*(screenshot to come: the setup wizard — docs/images/setup-wizard.png)*

1. **Your router.** The wizard probes for your gateway and lists the
   candidates it found, with addresses and ping times; pick yours. The
   router becomes the root of your path to the internet, and is marked
   **critical** — you'll be told when it's down even if the internet is
   somehow still up.
2. **Internet anchors.** Four well-known public DNS services (Google,
   Cloudflare, Quad9, OpenDNS), all checked by default. These are the
   witnesses the verdict trusts: the internet is up when at least one of
   them answers. Keep all four.
3. **Which public resolver do you use?** If your network resolves names
   through one of those services, say which — it lets ConnectionWatcher
   tell *your resolver is down* from *DNS is down everywhere*. "I don't
   know" is a fine answer.
4. **Your router's public address (optional).** The wizard can probe for
   your router's WAN address and add it as a path hop, and asks who your
   internet provider is — that name is what lets the banner say "Comcast is
   not carrying your traffic" instead of naming an IP address.

Press **Start monitoring** and you're watching. (Moving from another
ConnectionWatcher instead? The wizard's "Copy everything from a running
ConnectionWatcher" section is the way — see
[Moving to a new machine](#moving-to-a-new-machine-clone--take-over).)

For better results later, you can add more of your ISP's equipment to the
path — see [Targets](#targets) below.

### Secure the admin pages

Out of the box, anyone on your LAN who can reach the port can both watch
*and* configure. On the **Settings** page, set an **admin password**: it
locks the Config, Settings, Data, Traceroute, and Setup pages (and their
APIs) behind a single site password, while the watching surfaces — the
dashboard, History, reports — stay open. A 🔒 button on the dashboard
enters admin mode for the browser session.

It stops casual tinkering, not attackers (see [SECURITY.md](SECURITY.md)
for the honest trust model). Forgot the password? Delete
`admin-password.txt` next to the database
(`C:\ProgramData\DSoftCo\ConnectionWatcher\data\` on Windows, the mapped
`data/` folder on Docker) and reload.

### The tray companion

*(screenshot to come: the tray icon and its menu —
docs/images/tray-menu.png)*

`conwatch-tray.exe` keeps the verdict in the corner of your eye:

- **Green** — healthy (including trouble that isn't yours: a site incident
  or internet weather). **Orange** — your link is degraded. **Red with a
  slash** — down, or name lookups are failing. **Muted green** — still
  measuring. **Gray** — the tray can't reach the ConnectionWatcher service
  at all. A small neutral dot on the icon means ConnectionWatcher has
  [notes](THEORY_OF_OPERATION.md#model-warnings-connectionwatcher-notes)
  for the administrator — never an alarm, just a "look when you get a
  minute."
- **Double-click** opens the dashboard; a **single click** (either button)
  opens the menu: Open ConnectionWatcher, ConnectionWatcher notes (when
  there are any), History, the ISP report, Server settings, the three
  scouting actions (below), Tray settings, and Exit.
- **Toasts** announce verdict changes ("Internet DOWN", "Internet healthy
  again", "Your internet is up, but name lookups are failing"), a critical
  target failing or recovering, and the service itself becoming
  unreachable. Turn them off in Tray settings if you'd rather just glance
  at the color.
- **Tray settings** holds the server URL (with a Test button and a
  "use installed default" reset), the toast toggle, and a "Forget saved
  admin password" button.

The tray runs happily on machines that aren't running the server — point
it at the NAS with `conwatch tray install --url http://nas:8080`, or just
run the installer with the Service box unchecked.

**Scouting.** The tray's *Find my router*, *Find the network edge*, and
*Scan this network* menu items run network searches **from your PC** and
post the results to the server's Discoveries inbox — exactly what you need
when the server runs in a container that can't see your LAN. If the server
has an admin password, the tray prompts for it; *Remember on this PC*
stores it encrypted for your Windows account, bound to that server, and
only after the server accepts it.

## Setting up targets and pools

The setup wizard gives you a working configuration; the **Config** pages
(admin-locked) are where you refine it. The full reasoning behind these
concepts lives in [THEORY_OF_OPERATION.md](THEORY_OF_OPERATION.md); here's
how to drive the editor.

### Targets

*(screenshot to come: the Targets page with the path pipeline —
docs/images/config-targets.png)*

**Config → Targets** shows your **path to the internet** as a pipeline —
router, then any hops beyond it, with "⊕ insert here" buttons to add a hop
at the right spot — and a table of everything being watched. Each target
has:

- **Name** and **address** — an IP, or a hostname. Hostname targets follow
  DNS: the name is re-resolved every five minutes (and immediately when the
  target goes Down), and an address that has left the answer set is
  replaced without resetting the target's history.
- **Probe** — ICMP ping by default, or a **TCP connect** to a chosen port
  for devices that firewall ping but still run a service (a refused
  connection still counts as a reply — an RST proves the host answered).
  Private addresses allow any port; public addresses are restricted to 80,
  443, and 53, where a bare connect-and-close looks like ordinary client
  traffic. The **Test** button tries ICMP and the policy-allowed TCP ports
  for the typed address in one click and preselects whichever answered;
  for a hostname it also lists every address in the DNS answer set.
- **Parent** — the hop this target sits behind, forming the reachability
  tree.
- **Profile** — the ping cadence bundle (see [Profiles](#profiles)).
- **Scope** — *Internet path* (counts toward the verdict) or *LAN device*
  (tracked on the side in the "How's my LAN" section, never affects the
  verdict).
- **Critical** — notify when this is down, even if the internet is fine.
  The wizard sets it on your router; a NAS or a key switch are good
  candidates too.
- **ISP / operator** (optional) — "Comcast". On a path hop this is how
  ConnectionWatcher knows which anchors belong to your ISP and whose name
  to put in the blame line; on anchors it groups addresses by provider so
  four Google addresses failing together stay one site incident, never
  internet weather.

Adding your ISP's equipment to the path is the single best upgrade to the
blame line: run **Traceroute** (its own page), see the hops between you
and a destination labelled Internal / Path / Destination, and add any of
them as a target with parent and profile pre-filled. (Settings →
Traceroute can also look up each public hop's operator through Team
Cymru's IP-to-ASN service, so the operator name is pre-filled too.) Be
aware that ISPs rearrange their networks from time to time, so those
addresses may be reassigned later — if a hop starts misbehaving, the
[notes](THEORY_OF_OPERATION.md#model-warnings-connectionwatcher-notes)
will tell you.

**Editing an address** asks a three-way question — correcting a mistake,
pointing at a different device, or the same device renumbered — because
the first two mean the old measurements belong to something else and the
history should break, while a renumber is one continuous series. Only you
know which.

### Pools

**Config → Pools** manages rotation groups that share the pinging load:
members are phase-staggered, so a four-member pool pinged every 10 s
yields a data point every 2.5 s while each site sees one polite ping per
10 s. The pool chosen as the **verdict anchor pool** supplies the internet
anchors the verdict reads; the same form sets how many distinct operators
must be degraded before trouble counts as internet-wide weather (default
2). Keep at least three anchors from different operators — the config
health banner will nag you if the pool gets thin or if every anchor
belongs to your ISP.

### Profiles

**Config → Profiles** holds the ping cadence bundles. Five are built in —
**Internal** (your own LAN, 1 s), **Path** (hops to the internet you don't
own, 5 s), **Public** (sites where you should be polite, 10 s), **Local**
(LAN devices, tolerant of sleepy IoT gear), and **Resolver** — and each
carries a description you can edit. Every knob (intervals, burst length,
timeout, window sizes, thresholds) is editable per profile.

### DNS

**Config → DNS** controls the name-resolution watch: the resolvers your
machine actually uses (found from your network settings, with a per-row
disable switch), any public resolver you want asked directly, and the
lookup names used for the queries. The seeded names are checked against
their published answers, which is how ConnectionWatcher can tell a
filtering resolver (a Pi-hole) from a lying or dead one.

### Discoveries

**Config → Discoveries** is the inbox for *found things*: **Find my
router**, **Find the network edge**, and **Scan local network** run
searches from the server (nothing ever scans automatically), and the
Windows tray's scouting items post results here from a PC's vantage point.
Each candidate can be **adopted** — which opens the target dialog
pre-filled sensibly (a gateway becomes a critical "Router"; an edge
becomes an "ISP edge" path hop; a LAN device gets Local scope) — or
hidden.

### Backup

Whole-config **export and import** live on the Targets page. A config
export is tied to the version that wrote it; export again after upgrading.

## What the reports tell you

- **Dashboard** (`/`) — the live verdict banner with an uptime clock, a
  color-coded status timeline (one dot per minute: warm colors are your
  path, monochrome is trouble out on the internet, the more intense the
  spot the more serious), the reachability tree with per-target success
  rate and latency, "How's my LAN", "Name resolution", a ledger of recent
  important events ("Your link went down for 4 min"), recent minor
  glitches, and — for the admin — ConnectionWatcher's notes.
- **History** (`/History`) — pick any subject (the verdict, a pool, a
  target, a resolver, a LAN device) and a range: uptime stats that keep
  *outages* separate from *unreachable — no data*, a state timeline,
  latency and loss charts, a **recurrence punchcard** (24 hours × 30 days
  — recurring 3 AM trouble shows up as a horizontal strip), and a
  cause-aligned ribbon board where one incident reads as a vertical stripe
  across exactly the rows it touched.
- **ISP report** (`/Report`) — the printable evidence pack: outage and
  degradation totals with incident-by-incident timestamps (shown in both
  local time and UTC so your provider can correlate), a brief-disturbances
  section for the glitches, and per-target uptime. The thing you attach to
  the support ticket.
- **Data** (`/Data`) — database stats, exports (transitions, rollups, and
  glitches as CSV or JSON; a sanitized snapshot JSON with all settings and
  data except secrets), and the retention settings that keep the database
  from growing forever.
- **Settings** (`/Settings`) — the monitoring master switch (off = archive
  mode: history and reports keep serving, pinging and notifications stop),
  notifications, the admin password, and the danger zone (clear history /
  prune / factory reset, type-to-confirm).

## How it decides

The short version: the internet is up when at least one anchor outside
your ISP answers. The path — router, modem, ISP hops — never declares the
internet down by itself; it says *where* the problem is once the anchors
agree it's down. A path hop that stops answering pings while traffic
visibly flows past it doesn't change the verdict; it earns a quiet
administrator's note instead. Restarts are warm (the timeline stays
continuous), detection is burst-accelerated (an outage is classified in
about 25–30 seconds), and sub-outage blips are recorded as glitches with
their own recurrence views.

The long version — states, thresholds, anchors, blame, loss attribution,
the name-resolution watch, and the rest — is
[THEORY_OF_OPERATION.md](THEORY_OF_OPERATION.md).

## Advanced features

### Running in Docker

```bash
docker run -d --name conwatch --restart unless-stopped \
  -p 8080:8080 -v ./data:/app/data --cap-add NET_RAW \
  --stop-timeout 30 ghcr.io/mdraughn/conwatch:latest
```

Open `http://localhost:8080` and the setup wizard takes it from there. The
image is multi-arch (amd64 + arm64: Intel/ARM Synology, Raspberry Pi, any
Linux); `deploy/docker/compose.yaml` has the same thing in compose form,
including the 30 s stop grace period that lets the last minute of rollups
be written on the way out. The image runs as root by default so ICMP works
out of the box; if you harden it with a non-root user, keep `NET_RAW` so
ping and traceroute keep working.

Image tags: `latest` is the newest stable release (also `1.2.3`, `1.2`,
`1` to pin a line); `edge` is the newest build from `master`, for people
who like to live there.

Container caveats: on Synology, create the `./data` folder yourself before
the first start (Synology's Docker does not auto-create bind-mount
folders). Router autodetect inside a bridge network finds the container
bridge rather than your home router, LAN scans see only the container's
subnet, and the container's resolver (labelled "Docker's resolver") is
what the name-resolution watch measures. On a Linux host,
`network_mode: host` fixes the visibility problems; on any host, the
Windows tray's scouting items search from a real PC and post the results
to the container. [deploy/docker/README.md](deploy/docker/README.md) has
the full bridge-vs-host rundown and Synology notes.

### Moving to a new machine (clone + take over)

The setup wizard on a fresh install offers **"Copy everything from a
running ConnectionWatcher"**: enter the old node's URL (and its admin
password, if set) and the new node arrives with the full configuration,
history, and — over the authenticated channel — the notification secrets.
It starts as a **disabled read-only archive** while the old node keeps
monitoring; verify it looks right, then press **Take over** on its
Settings page: the old node is disabled, one final snapshot closes the
data gap to seconds, and the new node goes live (warm-started, so the
timeline stays continuous). The old node remains a browsable archive until
you decommission it.

Transport honesty: cloning over plain HTTP to a non-local address sends
the password and receives secrets unencrypted — the wizard stops and
offers the HTTPS address, an explicit "send it anyway", or a secret-less
clone.

### MQTT / Home Assistant

Configured on the **Settings** page under *Notifications*; off by default
and reconfigures live — no restart needed. Enter your broker (host, port,
optional credentials), press *Test connection*, and enable.
ConnectionWatcher publishes Home Assistant discovery documents, so
entities appear automatically under one "ConnectionWatcher" device:

- a **verdict sensor** (`Healthy` / `SiteIncident` / `InternetWeather` /
  `LinkDegraded` / `DnsDown` / `LinkDown`, plus `Unknown` while measuring,
  with the detail text and DNS blame as attributes),
- an **internet binary sensor** (OFF only when the verdict is LinkDown or
  DnsDown — the simplest summary),
- a **name-resolution binary sensor** (ON while your resolvers answer, OFF
  when none does; *unavailable* while the watch is off; attributes carry
  the resolver lists and the blame),
- a **connectivity binary sensor per target** (ON unless Down or
  Unreachable; attributes carry `critical` and `scope`).

State topics are retained under the base topic (default `conwatch/…`),
with a last-will `availability` topic so entities show *unavailable* if
ConnectionWatcher stops. A target named *Internet* or *DNS* gets a
suffixed entity (`internet_2`, `dns_2`) so it cannot overwrite the fixed
sensors; after upgrading, delete the old entity in Home Assistant if one
lingers. Every transition is also published as JSON to
`conwatch/transitions` for automations that want the raw events.
Publishes happen only on state changes and reconnects, plus a
once-a-minute re-check of the name-resolution sensor that publishes only
when it changed — never per ping.

### Webhooks

One URL per line on the Settings page; each transition is POSTed as JSON
(`subjectKind`, `subjectName`, `from`, `to`, `trigger`, success/loss
numbers, the verdict, and the root-cause target in `ancestorId`); a
`DnsDown` verdict change also carries a `dns` block naming the resolver
that stopped answering. `timestamp` is ISO-8601 with an explicit `+00:00`
offset. Two filters: *Verdict changes only* notifies just when your
internet weather changes rather than on every target flap, and *Skip
explained transitions* (on by default) sends one message per root cause
instead of one per device behind a dead router. A **critical** target's
failures and recoveries always get through both filters.

### Heartbeat

Point the heartbeat URL (Settings → Notifications) at a dead-man's-switch
service (healthchecks.io or self-hosted) and ConnectionWatcher GETs it
every few minutes (the interval is yours to set) to say "still alive"; the service alerts you when the
pings stop — a dead NAS, a crashed container, or a full internet outage.
Watches the watchman.

### Ask Claude about your internet (MCP)

ConnectionWatcher exposes a Model Context Protocol endpoint at `/mcp`, so
an MCP host can query the node directly:

```bash
claude mcp add --transport http conwatch http://nas.example.com:8080/mcp
```

Then just ask — "how was my internet last night?", "any glitches today?",
"what did the verdict blame on Tuesday?" — and Claude answers from the
actual database via five read-only tools: `get_current_status`,
`get_connection_report`, `get_transitions`, `get_glitches`, and
`list_discoveries` (that last one follows the admin gate, since the
discoveries inbox is a network inventory). The endpoint is open like the
rest of the watching surface and serves nothing the open APIs don't
already serve; it never touches settings, secrets, or admin actions.

### Grafana / Prometheus

ConnectionWatcher exposes Prometheus metrics at `/metrics` — open like the
rest of the watching surface, and serving nothing `/api/v1/status`
doesn't. Scrape config:

```yaml
scrape_configs:
  - job_name: conwatch
    scrape_interval: 15s
    static_configs:
      - targets: ["nas.example.com:8080"]
```

What you get: `conwatch_verdict{state=...}` (one-hot — feed it straight to
a Grafana state-timeline panel), `conwatch_dns_up`, per-target
`conwatch_target_up`, `conwatch_target_loss_ratio` (packet loss, 0..1),
`conwatch_target_rtt_seconds` and `conwatch_target_sample_size`,
`conwatch_pool_up`, `conwatch_transitions_total` and
`conwatch_glitches_total` counters for `rate()` panels, plus
`conwatch_engine_enabled`, `conwatch_model_warnings`,
`conwatch_last_sample_age_seconds`, and `conwatch_build_info`. Per-target
series of Local-scope targets carry `scope="local"`, so a dashboard can
leave LAN devices out (`conwatch_target_up{scope!="local"}`) or chart them
alone. Useful starters:

```promql
conwatch_target_rtt_seconds * 1000            # per-target latency in ms
avg(conwatch_target_loss_ratio) * 100         # overall packet loss %
increase(conwatch_transitions_total[1h])      # instability by target
```

### Running unattended

**Zip install** — prefer a scripted or portable install over the Setup
wizard? Unzip `ConnectionWatcher-<version>-win-x64.zip` anywhere and, from
a terminal in that folder:

```powershell
conwatch service install --port 8080
conwatch tray install
conwatch service status
```

The first command asks for administrator rights once. Both installs are
the same folder and the same commands; the wizard just clicks them for
you. The binaries are self-contained — no .NET install needed.

**The `conwatch` command** does everything else:

| Command | What it does |
|---|---|
| `conwatch serve` | Run the watcher in the foreground (this is also what the service runs). `--url`, `--data` override the config. |
| `conwatch service install\|uninstall\|start\|stop` | Manage the Windows service (elevates once). Install takes `--port` or `--url`, `--data`, `--no-start`. |
| `conwatch service status` | Installed? Running? Which port and data folder? Does the dashboard answer? |
| `conwatch config show` | Every setting and where it came from. |
| `conwatch config set listen-url\|data-dir\|server-url <value>` | Change one; listen-url/data-dir take effect on the next service start. To change the port use `service install --port`. |
| `conwatch tray` / `conwatch tray install\|uninstall [--url]` | Start the tray now / at sign-in. |
| `conwatch version` | Print the version and build stamp. |

Settings live in `%ProgramData%\DSoftCo\ConnectionWatcher\conwatch.json`
(listen URL, data folder, the tray's server URL) with a per-user override
in `%AppData%\DSoftCo\ConnectionWatcher\tray.json` written by the tray's
Settings dialog or `conwatch config set server-url <url> --user`. Data
stays in `%ProgramData%\DSoftCo\ConnectionWatcher\data`, readable only by
SYSTEM and Administrators. Uninstalling never deletes either without
asking. To move the dashboard to another port, run
`conwatch service install --port <n>` again; it re-pins the port, firewall
rule and shortcut and restarts the service.

**Upgrading** — run the new installer over the old install; configuration
and data stay put. **Upgrading from 0.9** — the wizard replaces `ConWatch.exe` and
`ConTray.exe` with `conwatch.exe` and `conwatch-tray.exe`, re-registers
the service, moves your tray URL from `contray.json` into the new config
file, and removes the old `Watcher\` and `Tray\` folders. The tray reads
the machine config when it starts, so after `conwatch config set` restart
the tray to pick up a new installed default.

**Reverse proxy / HTTPS** — ConnectionWatcher is proxy-friendly by
construction: it emits only relative URLs and never reads scheme, host, or
client IP, so an HTTPS-terminating proxy (e.g. Synology DSM's reverse
proxy with the existing cert) needs no app-side configuration. Two rules:
proxy it at the **root of its own hostname** (sub-path mounting is
unsupported), and enable the proxy's **WebSocket** headers or the
dashboard's live updates stall. If a future feature consumes client IPs or
the original scheme, add `UseForwardedHeaders` then — noting that inside
Docker the proxy's requests arrive from the bridge gateway, not loopback,
so the trusted-proxy config must name the bridge network.
