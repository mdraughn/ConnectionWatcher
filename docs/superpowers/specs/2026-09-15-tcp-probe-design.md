# TCP-Connect Probe

*2026-09-15 — designed with Mark; motivating case: the Comcast Business
gateway in bridge mode serves its admin page at 10.1.10.1 but firewalls
ICMP, so today the only probeable address it offers is its hairpinned
management WAN IP (jittery ~25 ms baseline, drifts with the DHCP lease).*

## Goal

A per-target probe method: ICMP echo (today's behavior, the default) or a
TCP connect to a chosen port. TCP-probed targets are **full citizens** —
same state machine, burst probing, glitches, rollups, warm restart, and
reports. The probe method is swapped at the lowest layer; everything above
`PingResult` is unchanged.

## Semantics: what counts as a reply

A probe cycle opens **one fresh socket**, races `ConnectAsync` against the
profile's ping timeout, sends **no data**, and closes cleanly (FIN) on
success. Stopwatch time to the outcome is the RTT (milliseconds, as today).

| Outcome | Result | Message |
|---|---|---|
| Connection accepted (SYN-ACK) | success | `Accepted` |
| Connection refused (RST) | **success** | `Refused` |
| Timeout / silence | failure | `TimedOut` |
| ICMP unreachable / network error | failure | `SocketError` name |

**Refused counts as a reply** because an RST proves the host's stack
answered and the path works — this tool asks "can I reach this device,"
not "is the service healthy." A security-conscious host *drops* unwanted
SYNs (reads as down, correctly); a host that RSTs is by definition not
hiding. The Accepted/Refused distinction survives in the message so
transition details can show it.

**Persistent-refusal warning (politeness valve):** when a TCP target has
been refusing essentially every probe for a sustained stretch (24 h),
`ConfigHealth` emits the standard ⚠ warning on the Targets page: nothing
is listening on that port, use the Test button to find a better one. No
back-off machinery, no verdict change — a config nudge, matching how the
app already handles pool-parent drift.

## Port policy: polite by address class

- **Private addresses** (RFC1918, loopback, link-local): **any port**.
  Your network, your gear — this covers the motivating 10.1.10.1 case.
- **Public addresses**: TCP restricted to **80, 443, 53** — ports where a
  bare connect-and-close is indistinguishable from normal client traffic.
  Enforced by `WatcherOptionsValidator` with a clear message. An override
  can be added later if a real owned-public-odd-port case ever appears;
  start strict.
- Hostname targets are treated as **public** for the port policy (the
  validator sees the configured string, not the resolution, and hostnames
  resolve to public space in practice).

## Configuration: one field

`Probe` — optional string on the target, absent/null = ICMP (so every
existing config, snapshot, and appsettings seed is valid unchanged).
Format: `tcp:<port>` (1–65535). Parsed/validated in
`WatcherOptionsValidator`; parsed form is a `ProbeSpec` record in ConCore:

```csharp
public enum ProbeKind { Icmp, Tcp }
public readonly record struct ProbeSpec(ProbeKind Kind, int Port = 0)
{
    public static ProbeSpec Icmp => default;
    // Parse("tcp:80") / ToString() round-trip; null/empty => Icmp.
}
```

The field crosses the same ~13 explicit mapping lists the `Operator` field
did (Target → TargetConfig → TargetGraph → TargetRow (+ EF migration) →
TargetDto → ConfigStore load/seed/export → ConfigService upsert /
insert-parent / import ×2 → Targets form + its two JS field lists).
`ConfigService.InsertParentAsync` is the historically-forgotten one.

## Engine: threading the probe to the wire

The validator already rejects duplicate addresses, so an IP has exactly
one probe config — **the IP-keyed controller table, routing table, and
result fan-out stay unchanged**. Same-host-two-probes is a non-goal
(already impossible for addresses generally).

- `IPingTransceiverFactory.Create(IPAddress, CancellationToken)` widens to
  `Create(IPAddress, ProbeSpec, CancellationToken)`. Four implementors
  update: the real ConInfrastructure factory dispatches on `Kind` (ICMP
  transceiver as today, TCP below); `FakePingTransceiverFactory`,
  `PingTransceiverInteractiveSimFactory`, and
  `ConfiguredPingTransceiverFactory` accept-and-record the new argument.
- New `TcpConnectTransceiver` in ConInfrastructure:
  fresh `Socket` per `PingAsync`, `ConnectAsync` raced with
  `Task.Delay(timeoutMs)` and the stored token (the OS SYN-retry schedule
  ignores caller timeouts — the race enforces ours), refused caught via
  `SocketException(ConnectionRefused)` → success/`Refused`. Dispose always.
- `WatcherEngine.Activate`'s burst-timeout clamp (`min(steady, 1000)` ms)
  applies as-is: a live LAN/WAN handshake fits well under a second, and
  our own race enforces the deadline regardless of OS retries.
- DNS resolution path unchanged — TCP probes resolve hostnames exactly as
  ICMP targets do and probe the resolved IPv4 address.
- **Warm restart:** `TargetSeed` staleness equality extends from Address
  to Address + Probe, so switching a target from ICMP to TCP discards the
  presumed state instead of adopting it.

## Probe test: one-click auto-configure

New admin-gated endpoint `POST /api/v1/targets/probe-test { address }`:
the **server** (where monitoring actually runs) concurrently tries ICMP
plus TCP connects to the policy-allowed ports for that address class
(private: 80, 443, 8080, 8443 + any port already typed in the form;
public: 80, 443, 53), ~2 s timeout each, and returns per-probe
`{ probe, reply, rttMs, message }`.

Targets form gains a **Test** button beside the address field. It renders
the results inline (`ICMP: no reply · tcp/80: reply 1 ms · …`) and
preselects the winner: ICMP if it answered, else the best-RTT TCP port.
The Probe row itself is a two-state select (`Ping (ICMP)` default /
`TCP connect`) with a Port box that appears for TCP.

Display: targets table and dashboard tree render a TCP target's address
as `10.1.10.1 · tcp/80`; ICMP targets look exactly as today.

## Copy fixes in passing

- Report page: "continuous ICMP monitoring" → wording that covers both.
- `IPingTransceiver` / `PingResult` doc comments drop the ICMP-only claim.

## Testing

- `ProbeSpec` parse/validate unit tests (incl. port policy by address class).
- Fake: `FakePingTransceiverFactory` learns the probe (records it like
  `ObservedTimeouts`; `SetUp` unchanged) so engine tests can assert a TCP
  target runs the ordinary discipline: transitions, bursts, glitches.
- Real-socket integration test: `TcpConnectTransceiver` against a local
  `TcpListener` (accepted), a closed local port (refused ⇒ success), and
  a non-routable address (timeout ⇒ failure).
- Round-trip test mirroring `OperatorFieldTests`: create/update/export,
  import + snapshot round-trip, absent field defaults to ICMP.
- Probe-test endpoint test (fake transceivers; admin-gated; policy ports).
- ConfigHealth persistent-refusal warning test.

## Non-goals

- Two probes against one host (validator already forbids duplicate addresses).
- TLS/HTTP-level checks — this is reachability, not service health.
- IPv6 (matches the existing resolver's IPv4-only behavior).
- Back-off/cadence changes on refusal (the ConfigHealth warning is the valve).
- Traceroute/gateway-discovery changes (they use `Ping` directly, unaffected).
