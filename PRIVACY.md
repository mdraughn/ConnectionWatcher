# Privacy

ConnectionWatcher has **no telemetry**: nothing phones home to the author or
anyone else — no usage analytics, no crash reporting, no version checks.

## The complete list of network traffic the app can generate

- **ICMP pings, TCP connects, and DNS queries for the targets you
  configured.** That's the product. Public addresses are never probed faster
  than their profile allows, and TCP probes to public addresses are
  restricted to ports 80, 443, and 53, where a bare connect-and-close looks
  like ordinary client traffic.
- **The name-resolution watch's DNS queries.** It asks the resolvers your
  machine is configured with — and any public resolver you named — for a
  short list of lookup names (seeded with `dns.google`, `one.one.one.one`,
  `dns.quad9.net`, and `dns.opendns.com`, editable in the `Dns.LookupNames`
  setting) and for the names of your hostname targets. Turn the watch off
  (`Dns.Enabled`) and those queries stop.
- **Only if you enable them, and only to endpoints you typed in:** MQTT to
  your broker, webhooks to your URLs, the heartbeat ping.
- **Only when you trigger them:** the setup wizard's router probe (your own
  gateway), traceroute, LAN and router discovery scans, clone/take-over
  calls to a node you named, and — if you turn it on in Settings →
  Traceroute — operator lookups of public traceroute hops through Team
  Cymru's IP-to-ASN DNS service.

## No third-party requests from your browser

The web UI is self-contained — CSS, fonts, and scripts are served by the
app, so browsers make no third-party requests.

## Logging

Logging goes to stdout only (your container's log, or Warning+ to the
Windows Event Log as a service); nothing is written to log files,
credentials are never logged, and webhook/heartbeat URLs appear in logs
with their secret paths elided (`https://hc-ping.com/…`) so a pasted log
can't leak them.

## What leaves the node in exports

Exports and the snapshot are sanitized: MQTT credentials, webhook URLs, and
the heartbeat URL never leave the node through open endpoints. The one
channel that transfers secrets — clone/take-over — requires the admin
password and refuses to send them over plain HTTP to a non-local address
unless you explicitly override. See [SECURITY.md](SECURITY.md) for the full
trust model.
