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
  scope?: "Verdict" | "Local" | "Resolver";
  /** Notify when this is down, even if the internet is fine (spec §4.7). */
  critical?: boolean;
  /** Created from the host's OS resolver list; the admin can only disable it. */
  systemManaged?: boolean;
  /** Id of the upstream target whose failure explains this one's Unreachable state. */
  explainedBy?: string | null;
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
  kind: "HopBypassed" | "NeverAnswered" | "UnprovableUp" | "ProbeOnlyLoss" | "ResolverPartial" | "ResolverLying" | "ResolverFiltered" | "UpstreamResolverDown" | "NameUnresolvable" | string;
  targetId?: string | null;
  targetName?: string | null;
  message: string;
  since: string;
  acknowledged: boolean;
}

export interface DnsResolverStatus { id: string; address: string; label: string; state: string; detail: string | null; enabled: boolean; }
export interface DnsLookupNameStatus { name: string; lastResult: "ok" | "miss" | "filtered" | "none" | string; detail: string | null; }
export interface DnsBlame { kind: "YourResolver" | "NoResolver" | string; message: string; resolvers: string[]; }
export interface DnsStatus {
  watched: boolean; state: "Unknown" | "Ok" | "Degraded" | "Filtered" | "Down" | string;
  systemResolvers: DnsResolverStatus[]; upstreamResolvers: DnsResolverStatus[];
  blame: DnsBlame | null; lookupNames: DnsLookupNameStatus[];
}

export interface Snapshot {
  enabled?: boolean;
  verdict: VerdictStatus | null;
  targets: TargetStatus[];
  pools: PoolStatus[];
  warnings?: ModelWarning[];
  dns?: DnsStatus | null;
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
  scope?: "Verdict" | "Local" | "Resolver";
}

export interface RollupPoint {
  periodStart: string;
  pingCount: number;
  successCount: number;
  rttAvg: number;
  rttMin: number;
  rttMax: number;
}
