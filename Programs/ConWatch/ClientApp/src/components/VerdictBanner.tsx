// The verdict slab answers ONE question at a glance: is my internet up?
// The big line is always one of three plain answers (UP / STRUGGLING / DOWN);
// the diagnosis is a smaller caveat line in plain language — no anchors, no
// probe counts — wearing the shared encoding's mark so it rhymes with the
// ledger. The technical detail survives as a hover tooltip.
import type { Mark } from "../lib/encoding";
import type { DnsStatus, VerdictStatus } from "../lib/types";
import { type AnswerClock } from "../lib/verdictClock";
import { formatClock } from "../lib/format";
import { MarkSwatch } from "./marks";

interface Slab {
  cls: string;          // reuses the existing color classes
  icon: "up" | "struggling" | "down" | "checking";
  headline: string;
  caveat: string | null;
  caveat2: string | null; // a second small line, no mark
  mark: Mark | null;    // whose-gear chip beside the caveat
}

/** The Signal glyphs (docs/design/web-ui/lib-icons.mjs, direction A): the
 *  brand mark carrying state — reach shrinks as things get worse, and down
 *  is the dimmed glyph under a slash so it survives greyscale. */
function SignalIcon({ state, size = 26 }: { state: Slab["icon"]; size?: number }) {
  const dot = <circle cx="12" cy="12" r="2.6" />;
  const inner = <>
    <path d="M16.6 8.7a5.2 5.2 0 0 1 0 6.6" fill="none" />
    <path d="M7.4 15.3a5.2 5.2 0 0 1 0-6.6" fill="none" />
  </>;
  const outer = <>
    <path d="M19.4 5.9a8.6 8.6 0 0 1 0 12.2" fill="none" />
    <path d="M4.6 18.1a8.6 8.6 0 0 1 0-12.2" fill="none" />
  </>;
  const g = (children: React.ReactNode, opacity?: number) => (
    <g stroke="currentColor" fill="currentColor" strokeWidth={2}
      strokeLinecap="round" strokeLinejoin="round" opacity={opacity}>{children}</g>
  );
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" style={{ display: "block", flexShrink: 0 }}>
      {state === "up" && g(<>{dot}{inner}{outer}</>)}
      {state === "struggling" && g(<>{dot}{inner}</>)}
      {state === "checking" && g(<>{dot}{inner}{outer}</>, 0.55)}
      {state === "down" && <>
        {g(<>{dot}{inner}{outer}</>, 0.32)}
        <path d="M5 19 19 5" stroke="currentColor" strokeWidth={2.6}
          strokeLinecap="round" fill="none" />
      </>}
    </svg>
  );
}

export function slabFor(verdict: VerdictStatus, dns?: DnsStatus | null): Slab {
  switch (verdict.state) {
    case "Healthy":
      return {
        cls: "healthy", icon: "up", headline: "Your internet is UP",
        caveat: "no trouble in sight", caveat2: null, mark: null,
      };
    case "SiteIncident":
      return {
        cls: "healthy", icon: "up", headline: "Your internet is UP",
        caveat: "one site out there is having trouble — not your connection",
        caveat2: null,
        mark: { shape: "dot", hue: "warm", depth: "glitch" },
      };
    case "InternetWeather":
      return {
        cls: "healthy", icon: "up", headline: "Your internet is UP",
        caveat: "but parts of the internet look unreachable right now — the trouble isn't on your end",
        caveat2: null,
        mark: { shape: "bar", hue: "warm", depth: "degraded" },
      };
    case "LinkDegraded":
      return {
        cls: "linkdegraded", icon: "struggling", headline: "Your internet is STRUGGLING",
        caveat: "your connection is losing some traffic — pages may stall or feel slow",
        caveat2: null,
        mark: { shape: "bar", hue: "grey", depth: "degraded" },
      };
    case "LinkDown":
      return {
        cls: "linkdown", icon: "down", headline: "Your internet is DOWN",
        caveat: verdict.operator
          ? `${verdict.operator} is not carrying your traffic`
          : "your connection to the internet is not carrying traffic",
        caveat2: dns?.state === "Down" ? "Name lookups are failing too" : null,
        mark: { shape: "bar", hue: "grey", depth: "out" },
      };
    case "DnsDown":
      return {
        cls: "linkdown", icon: "down", headline: "Your internet is UP, but name lookups are failing",
        caveat: (verdict.detail ?? "").replace(/^DnsDown:\s*/, "") || "your resolver isn't answering",
        caveat2: null,
        mark: { shape: "bar", hue: "grey", depth: "out" },
      };
    default:
      return {
        cls: "unknown", icon: "checking", headline: "Checking your internet…",
        caveat: "measuring — an answer arrives within a minute or two", caveat2: null, mark: null,
      };
  }
}

const CLOCK_LABEL: Record<string, string> = {
  up: "up for", struggling: "struggling for", down: "down for",
};

/** The slab's clock: how long the current answer has held. The up-streak
 *  only resets on DOWN (decided with Mark: struggling blemishes a streak,
 *  it doesn't end it), so struggling shows the still-counting streak
 *  beneath the stretch timer, and down shows the streak the outage ended. */
function Clock({ clock }: { clock: AnswerClock }) {
  if (!clock.sinceIso || !(clock.cls in CLOCK_LABEL)) return null;
  const sinceMs = Date.now() - Date.parse(clock.sinceIso);
  return (
    <div className="vb-clock"
      title={`since ${new Date(clock.sinceIso).toLocaleString()}`}>
      <div className="lbl">{CLOCK_LABEL[clock.cls]}</div>
      <div className="num">{formatClock(sinceMs)}{clock.atLeast ? "+" : ""}</div>
      {clock.upStreakMs !== null && (
        <div className="prev">
          {clock.cls === "struggling"
            ? `up ${formatClock(clock.upStreakMs)}`
            : `up ${formatClock(clock.upStreakMs)} before this`}
        </div>
      )}
    </div>
  );
}

export function VerdictBanner({ verdict, dns, connected, clock }: {
  verdict: VerdictStatus | null;
  dns?: DnsStatus | null;
  connected: boolean;
  clock?: AnswerClock | null;
}) {
  if (!verdict) {
    return (
      <div className={`verdict-banner unknown ${connected ? "" : "reconnecting"}`}>
        &#9675; VERDICT OFF — pick internet check sites (an anchor pool) on the Admin page
        {!connected && <span className="pill">reconnecting…</span>}
      </div>
    );
  }
  const slab = slabFor(verdict, dns);
  // The icon sits in its own left column so the headline and the caveat
  // share one text edge: icon, "Your internet is UP", and directly under
  // the Y, "no trouble in sight".
  return (
    <div className={`verdict-banner ${slab.cls} ${connected ? "" : "reconnecting"}`}
      title={verdict.detail ?? undefined}>
      {!connected && <span className="pill">reconnecting…</span>}
      <div style={{ display: "flex", gap: 12, alignItems: "flex-start", flexWrap: "wrap" }}>
        <span style={{ flexShrink: 0, marginTop: 2 }}><SignalIcon state={slab.icon} /></span>
        <div style={{ minWidth: 0 }}>
          <div style={{ fontSize: "1.25rem", lineHeight: 1.3 }}>{slab.headline}</div>
          {slab.caveat && (
            <div style={{ fontSize: "0.8rem", fontWeight: 400, opacity: 0.92, marginTop: 2 }}>
              {slab.mark && <span style={{ marginRight: 6, verticalAlign: "baseline" }}>
                <MarkSwatch mark={slab.mark} barWidth={13} size={8} />
              </span>}
              {slab.caveat}
            </div>
          )}
          {slab.caveat2 && (
            <div style={{ fontSize: "0.8rem", fontWeight: 400, opacity: 0.92, marginTop: 2 }}>
              {slab.caveat2}
            </div>
          )}
        </div>
        {clock && <Clock clock={clock} />}
      </div>
    </div>
  );
}
