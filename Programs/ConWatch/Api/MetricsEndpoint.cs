using System.Globalization;
using System.Text;
using ConEngine;
using ConEngine.Topology;
using ConEngine.Tracking;
using ConCore.Targets;
using ConWatch.Metrics;

namespace ConWatch.Api;

/// <summary>
/// GET /metrics: Prometheus text exposition of the live snapshot, open like
/// the rest of the watching surface (it says nothing /api/v1/status doesn't).
/// Hand-rolled — a handful of families read straight from the in-memory
/// engine needs no exporter dependency and drags in no default metrics.
/// </summary>
public static class MetricsEndpoint
{
    public static void MapMetrics(this WebApplication app) =>
        app.MapGet("/metrics", (WatcherHostService host, MetricsCounters counters,
            ConPersistence.Warnings.ModelWarningLedger warnings, TimeProvider time) =>
            Results.Text(Render(host.Engine, counters, warnings, host.EngineEnabled, time.GetUtcNow()),
                "text/plain; version=0.0.4", Encoding.UTF8));

    internal static string Render(WatcherEngine? engine, MetricsCounters counters,
        ConPersistence.Warnings.ModelWarningLedger warnings, bool enabled, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        var localNames = engine is null
            ? new HashSet<string>()
            : engine.Monitors.Values.Where(m => m.Target.Scope == TargetScope.Local)
                .Select(m => m.Target.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Family(sb, "conwatch_build_info", "gauge", "Build version as a label; the value is always 1.");
        sb.Append("conwatch_build_info{version=\"").Append(Escape(BuildInfo.Version)).Append("\"} 1\n");

        Family(sb, "conwatch_engine_enabled", "gauge", "1 when monitoring is enabled, 0 in archive mode.");
        sb.Append("conwatch_engine_enabled ").Append(enabled ? 1 : 0).Append('\n');

        // Admin notes only (spec §4.6, D3): never part of up/verdict series.
        Family(sb, "conwatch_model_warnings", "gauge",
            "Active ConnectionWatcher notes (model warnings), acknowledged or not.");
        sb.Append("conwatch_model_warnings ").Append(warnings.Current.Count).Append('\n');

        if (engine?.LastResultUtc is { } last)
        {
            Family(sb, "conwatch_last_sample_age_seconds", "gauge", "Seconds since the engine last processed a ping result.");
            sb.Append("conwatch_last_sample_age_seconds ")
              .Append((now - last).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }

        if (engine is not null)
        {
            if (engine.Verdict is { } verdict)
            {
                Family(sb, "conwatch_verdict", "gauge", "One-hot: 1 on the current verdict state, 0 elsewhere.");
                foreach (var state in Enum.GetValues<VerdictState>())
                    sb.Append("conwatch_verdict{state=\"").Append(state).Append("\"} ")
                      .Append(verdict.CurrentVerdict == state ? 1 : 0).Append('\n');
            }

            var dns = ConEngine.Dns.DnsSummary.Compute(engine.DnsWatch, engine.DnsResults);
            if (dns.Watched && dns.State != ConEngine.Dns.DnsSummaryState.Unknown)
            {
                Family(sb, "conwatch_dns_up", "gauge", "1 while the host's name resolution works (Ok, Degraded or Filtered), 0 when every system resolver is down.");
                sb.Append("conwatch_dns_up ").Append(dns.State == ConEngine.Dns.DnsSummaryState.Down ? 0 : 1).Append('\n');
            }

            var targets = engine.Monitors.Values.OrderBy(m => m.Target.Name).ToList();

            Family(sb, "conwatch_target_up", "gauge", "1 while the target is responding.");
            foreach (var m in targets)
                Series(sb, "conwatch_target_up", m.Target.Name, m.CurrentState == HostState.Responding ? 1 : 0, localNames.Contains(m.Target.Name));

            Family(sb, "conwatch_target_loss_ratio", "gauge", "Packet loss over the sliding sample window (0..1).");
            foreach (var m in targets)
                Series(sb, "conwatch_target_loss_ratio", m.Target.Name, m.LastStatus?.Host.FailureRate ?? 0d, localNames.Contains(m.Target.Name));

            Family(sb, "conwatch_target_rtt_seconds", "gauge", "Average round-trip time over the sliding sample window.");
            foreach (var m in targets)
                Series(sb, "conwatch_target_rtt_seconds", m.Target.Name, (m.LastStatus?.Host.AverageRoundtripTime ?? 0d) / 1000.0, localNames.Contains(m.Target.Name));

            Family(sb, "conwatch_target_sample_size", "gauge", "Pings in the sliding sample window.");
            foreach (var m in targets)
                Series(sb, "conwatch_target_sample_size", m.Target.Name, m.LastStatus?.Host.SampleSize ?? 0, localNames.Contains(m.Target.Name));

            Family(sb, "conwatch_pool_up", "gauge", "1 while the pool is responding.");
            foreach (var p in engine.PoolCoordinators.OrderBy(p => p.Pool.Name))
                Series(sb, "conwatch_pool_up", p.Pool.Name, p.CurrentState == HostState.Responding ? 1 : 0);
        }

        Family(sb, "conwatch_transitions_total", "counter", "State transitions per subject since process start.");
        // Union with the live subjects: a counter key only springs into
        // existence on a subject's first transition, and warm restarts seed
        // silently — so without the union a stable target has NO series and
        // rate() reads "no data" instead of 0, blinding panels and alerts.
        var totals = counters.TransitionsSnapshot().ToDictionary(kv => kv.Key, kv => kv.Value);
        if (engine is not null)
        {
            foreach (var m in engine.Monitors.Values)
                totals.TryAdd(m.Target.Name, 0);
            foreach (var p in engine.PoolCoordinators)
                totals.TryAdd(p.Pool.Name, 0);
            if (engine.Verdict is not null)
                totals.TryAdd(VerdictCoordinator.SubjectName, 0);
        }
        foreach (var (name, count) in totals.OrderBy(kv => kv.Key))
            Series(sb, "conwatch_transitions_total", name, count, localNames.Contains(name));

        Family(sb, "conwatch_glitches_total", "counter", "Glitches (blips too short to be transitions) since process start.");
        sb.Append("conwatch_glitches_total ").Append(counters.Glitches).Append('\n');

        return sb.ToString();
    }

    private static void Family(StringBuilder sb, string name, string type, string help) =>
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
          .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

    private static void Series(StringBuilder sb, string family, string name, double value, bool local = false) =>
        sb.Append(family).Append("{name=\"").Append(Escape(name)).Append('"')
          .Append(local ? ",scope=\"local\"" : "")
          .Append("} ")
          .Append(value.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
