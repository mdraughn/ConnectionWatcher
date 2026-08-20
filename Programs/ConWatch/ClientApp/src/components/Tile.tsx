// Expanded row: state, evidence line, and (targets only) the 60-min chart.
import type { TransitionEvent } from "../lib/types";
import { MiniChart } from "./MiniChart";

const BURSTING = new Set(["Faltering", "Recovering", "Down"]);

export function Tile({ id, name, state, statsText, lastTransition, isPool, pinned, onTogglePin }: {
  id: string;
  name: string;
  state: string;
  statsText: string;
  lastTransition: TransitionEvent | null;
  isPool: boolean;
  pinned: boolean;
  onTogglePin: () => void;
}) {
  const quiet = state === "Responding";
  return (
    <div className={`cw-tile ${quiet ? "pinned-only" : ""}`} onClick={onTogglePin}>
      <span className={`dot ${state.toLowerCase()}`}></span>
      <b>{name}</b> — {state}
      <span className="stats" style={{ float: "right" }}>
        {statsText}
        {!isPool && BURSTING.has(state) ? " · burst probing" : ""}
        {pinned ? " · 📌" : ""}
      </span>
      {lastTransition && (
        <div className="why">
          {new Date(lastTransition.timestamp).toLocaleTimeString()} {lastTransition.from} → {lastTransition.to}
          {lastTransition.detail ? ` — ${lastTransition.detail}` : ""}
          {lastTransition.successRate != null
            ? ` — success ${(lastTransition.successRate * 100).toFixed(0)}%`
            : ""}
          {lastTransition.burstLossRate != null
            ? ` — burst loss ${(lastTransition.burstLossRate * 100).toFixed(0)}%`
            : ""}
        </div>
      )}
      {!isPool && (
        <div onClick={(e) => e.stopPropagation()}>
          <MiniChart targetId={id} />
        </div>
      )}
    </div>
  );
}
