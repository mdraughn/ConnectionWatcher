export interface TargetStatus {
  id: string;
  name: string;
  address: string | null;
  /** Optional probe override ("tcp:80"); absent/null means ICMP echo. */
  probe?: string | null;
  state: string;
  /** Warm-restart presumption not yet confirmed by fresh pings. */
  assumed?: boolean;
  successRate: number;
  avgRtt: number;
  sampleSize: number;
  /** Monotonic count of results ever recorded; the activity LED flickers when it grows. */
  pingCount?: number;
  /** "Verdict" (default when absent) or "Local": a LAN device tracked beside the verdict. */
  scope?: "Verdict" | "Local";
}

export interface PoolStatus {
  id: string;
  name: string;
  state: string;
}

export interface VerdictStatus {
  state: string;
  detail: string | null;
  /** ISP blamed by a Link* verdict ("Comcast"), else null. */
  operator?: string | null;
}

/** An admin note (model warning): where ConnectionWatcher's map disagrees
 *  with what the probes prove. Never part of the verdict. */
export interface ModelWarning {
  key: string;
  kind: "HopBypassed" | "NeverAnswered" | "UnprovableUp" | string;
  targetId?: string | null;
  targetName?: string | null;
  message: string;
  since: string;
  acknowledged: boolean;
}

export interface Snapshot {
  enabled?: boolean;
  verdict: VerdictStatus | null;
  targets: TargetStatus[];
  pools: PoolStatus[];
  warnings?: ModelWarning[];
}

export interface TransitionEvent {
  id?: number;
  timestamp: string;
  subjectId: string;
  subjectName: string;
  subjectKind: string;
  from: string;
  to: string;
  trigger: string;
  detail: string | null;
  verdict: string | null;
  successRate: number | null;
  sampleSize: number | null;
  burstLossRate: number | null;
  /** "Verdict" (default when absent) or "Local": a LAN device tracked beside the verdict. */
  scope?: "Verdict" | "Local";
}

export interface RollupPoint {
  periodStart: string;
  pingCount: number;
  successCount: number;
  rttAvg: number;
  rttMin: number;
  rttMax: number;
}
