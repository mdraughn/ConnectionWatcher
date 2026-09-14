using System.Globalization;
using System.Text;
using ConEngine;
using ConEngine.Topology;
using ConEngine.Tracking;
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
        app.MapGet("/metrics", async (WatcherHostService host, MetricsCounters counters) =>
        {
            var enabled = await host.EngineEnabledAsync();
            return Results.Text(Render(host.Engine, counters, enabled),
                "text/plain; version=0.0.4", Encoding.UTF8);
        });

    internal static string Render(WatcherEngine? engine, MetricsCounters counters, bool enabled)
    {
        var sb = new StringBuilder();

        Family(sb, "conwatch_build_info", "gauge", "Build version as a label; the value is always 1.");
        sb.Append("conwatch_build_info{version=\"").Append(Escape(BuildInfo.Version)).Append("\"} 1\n");

        Family(sb, "conwatch_engine_enabled", "gauge", "1 when monitoring is enabled, 0 in archive mode.");
        sb.Append("conwatch_engine_enabled ").Append(enabled ? 1 : 0).Append('\n');

        if (engine is not null)
        {
            if (engine.Verdict is { } verdict)
            {
                Family(sb, "conwatch_verdict", "gauge", "One-hot: 1 on the current verdict state, 0 elsewhere.");
                foreach (var state in Enum.GetValues<VerdictState>())
                    sb.Append("conwatch_verdict{state=\"").Append(state).Append("\"} ")
                      .Append(verdict.CurrentVerdict == state ? 1 : 0).Append('\n');
            }

            var targets = engine.Monitors.Values.OrderBy(m => m.Target.Name).ToList();

            Family(sb, "conwatch_target_up", "gauge", "1 while the target is responding.");
            foreach (var m in targets)
                Series(sb, "conwatch_target_up", m.Target.Name, m.CurrentState == HostState.Responding ? 1 : 0);

            Family(sb, "conwatch_target_loss_ratio", "gauge", "Packet loss over the sliding sample window (0..1).");
            foreach (var m in targets)
                Series(sb, "conwatch_target_loss_ratio", m.Target.Name, m.LastStatus?.Host.FailureRate ?? 0f);

            Family(sb, "conwatch_target_rtt_seconds", "gauge", "Average round-trip time over the sliding sample window.");
            foreach (var m in targets)
                Series(sb, "conwatch_target_rtt_seconds", m.Target.Name, (m.LastStatus?.Host.AverageRoundtripTime ?? 0f) / 1000.0);

            Family(sb, "conwatch_target_sample_size", "gauge", "Pings in the sliding sample window.");
            foreach (var m in targets)
                Series(sb, "conwatch_target_sample_size", m.Target.Name, m.LastStatus?.Host.SampleSize ?? 0);

            Family(sb, "conwatch_pool_up", "gauge", "1 while the pool is responding.");
            foreach (var p in engine.PoolCoordinators.OrderBy(p => p.Pool.Name))
                Series(sb, "conwatch_pool_up", p.Pool.Name, p.CurrentState == HostState.Responding ? 1 : 0);
        }

        Family(sb, "conwatch_transitions_total", "counter", "State transitions per subject since process start.");
        foreach (var (name, count) in counters.TransitionsSnapshot().OrderBy(kv => kv.Key))
            Series(sb, "conwatch_transitions_total", name, count);

        Family(sb, "conwatch_glitches_total", "counter", "Glitches (blips too short to be transitions) since process start.");
        sb.Append("conwatch_glitches_total ").Append(counters.Glitches).Append('\n');

        return sb.ToString();
    }

    private static void Family(StringBuilder sb, string name, string type, string help) =>
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
          .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

    private static void Series(StringBuilder sb, string family, string name, double value) =>
        sb.Append(family).Append("{name=\"").Append(Escape(name)).Append("\"} ")
          .Append(value.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
