// The incident ledger: each incident as one plain-English sentence, marked
// with the same encoding chip the punchcard uses. Placement-agnostic.
import type { LedgerItem } from "../lib/incidents";
import { MarkSwatch } from "./marks";

function when(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    weekday: "short", month: "short", day: "numeric",
    hour: "numeric", minute: "2-digit",
  });
}

export function Ledger({ items, days, maxItems = 15, moreHref }: {
  items: LedgerItem[];
  days: number;
  maxItems?: number;
  moreHref?: string;
}) {
  if (items.length === 0) {
    return <p style={{ opacity: 0.7 }}>Nothing to report in {days} days. Quiet is good.</p>;
  }

  const shown = items.slice(0, maxItems);
  const eventDays = new Set(items.map((i) => new Date(i.at).toDateString()));
  const quietDays = Math.max(0, days - eventDays.size);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
      {shown.map((item, i) => (
        <div key={i} style={{ display: "flex", gap: 12, alignItems: "baseline" }}>
          <span style={{ flexShrink: 0, alignSelf: "center" }}>
            <MarkSwatch mark={item.mark} barWidth={14} size={item.mark.shape === "dot" ? 9 : 12} />
          </span>
          <div style={{ fontSize: "0.9rem", lineHeight: 1.45 }}>
            <b>{when(item.at)}</b> — <b>{item.lead}</b>
            {item.body && <span style={{ opacity: 0.65 }}> {item.body}</span>}
          </div>
        </div>
      ))}
      {items.length > shown.length && moreHref && (
        <div style={{ fontSize: "0.85rem" }}>
          <a href={moreHref}>…and {items.length - shown.length} more on the History page</a>
        </div>
      )}
      <div style={{ borderTop: "1px solid rgba(128,128,128,0.25)", paddingTop: 8,
        fontSize: "0.85rem", opacity: 0.6 }}>
        The other {quietDays} of the last {days} days: nothing to report.
      </div>
    </div>
  );
}
