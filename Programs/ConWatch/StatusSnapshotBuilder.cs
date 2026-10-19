using ConCore.Targets;
using ConEngine;
using ConEngine.Topology;
using ConEngine.Tracking;
using ConPersistence.Warnings;

namespace ConWatch;

/// <summary>
/// What GET /api/v1/status answers, the SignalR statusChanged push carries,
/// the dashboard embeds for its first paint and the MCP status tool reports:
/// one shape. Positional records serialize in parameter order, which is the
/// order the anonymous objects they replace used.
/// </summary>
public sealed record StatusSnapshot(bool Enabled, StatusVerdict? Verdict,
    IReadOnlyList<StatusTarget> Targets, IReadOnlyList<StatusPool> Pools,
    IReadOnlyList<StatusWarning> Warnings, StatusDns? Dns)
{
    /// <summary>Archive mode (monitoring disabled): no live states.</summary>
    public static StatusSnapshot Disabled { get; } = new(false, null, [], [], [], null);
}

/// <summary>One resolver the name-resolution watch pings. <paramref name="Detail"/> is the
/// latest failure message while the resolver is not answering, else null.</summary>
public sealed record StatusResolver(Guid Id, string Address, string Label, string State, string? Detail, bool Enabled);

/// <param name="Kind">"YourResolver" or "NoResolver".</param>
public sealed record StatusDnsBlame(string Kind, string Message, IReadOnlyList<string> Resolvers);

/// <param name="LastResult">"ok" (Verified for a seeded name, Answered for any other), "miss", "filtered" or "none".</param>
public sealed record StatusLookupName(string Name, string LastResult, string? Detail);

/// <summary>The name-resolution watch (spec 2026-10-10): the summary state, the host's
/// resolvers and any upstream ones, the DnsDown blame, and each lookup name's last result.</summary>
public sealed record StatusDns(bool Watched, string State, IReadOnlyList<StatusResolver> SystemResolvers,
    IReadOnlyList<StatusResolver> UpstreamResolvers, StatusDnsBlame? Blame, IReadOnlyList<StatusLookupName> LookupNames);

/// <param name="Operator">The ISP blamed by a Link* verdict ("Comcast"), else null.</param>
public sealed record StatusVerdict(string State, string? Detail, string? Operator);

/// <param name="Operator">The target's operator/ISP tag, or null.</param>
/// <param name="SystemManaged">Created from the host's OS resolver list; the admin can only disable it.</param>
/// <param name="Critical">Notify when this is down, even if the internet is fine (spec §4.7).</param>
/// <param name="ExplainedBy">The root cause (target id) explaining this target's
/// Unreachable state, or null.</param>
public sealed record StatusTarget(Guid Id, string Name, string Address, string? Probe, string State,
    bool Assumed, double SuccessRate, double AvgRtt, int SampleSize, long PingCount, string? Operator,
    TargetScope Scope, bool Critical, Guid? ExplainedBy, bool SystemManaged);

public sealed record StatusPool(Guid Id, string Name, string State);

/// <summary>An admin note (model warning, spec 2026-10-09 §4.6): where the map
/// disagrees with what the probes prove. Read-only here; never part of the verdict.</summary>
/// <param name="Kind">"HopBypassed", "NeverAnswered" or "UnprovableUp".</param>
public sealed record StatusWarning(string Key, string Kind, Guid? TargetId, string? TargetName,
    string Message, DateTimeOffset Since, bool Acknowledged);

public static class StatusSnapshotBuilder
{
    public static StatusSnapshot? Build(WatcherEngine? engine, ModelWarningLedger warnings,
        IReadOnlyList<ConCore.Dns.DnsLookupName> lookupNames)
    {
        if (engine is null)
            return null;

        var verdict = engine.Verdict?.Current is { } c
            ? new StatusVerdict(c.State.ToString(), c.Detail, c.Operator)
            : null;
        var targets = engine.Monitors.Values.Select(m => new StatusTarget(
                m.Target.Id,
                m.Target.Name,
                m.ResolvedAddress?.ToString() ?? m.Target.Address,
                m.Target.Probe,
                m.CurrentState.ToString(),
                m.IsAssumed,
                m.LastStatus?.Host.SuccessRate ?? 0d,
                m.LastStatus?.Host.AverageRoundtripTime ?? 0d,
                m.LastStatus?.Host.SampleSize ?? 0,
                m.PingCount,
                m.Target.Operator,
                m.Target.Scope,
                m.Target.Critical,
                m.ExplainedBy,
                m.Target.SystemManaged))
            .OrderBy(t => t.Name).ToList();
        var pools = engine.PoolCoordinators
            .Select(p => new StatusPool(p.Pool.Id, p.Pool.Name, p.CurrentState.ToString()))
            .OrderBy(p => p.Name).ToList();

        var notes = warnings.Current.Select(w => new StatusWarning(w.Key, w.Kind.ToString(),
                w.TargetId, w.TargetName, w.Message, w.SinceUtc, w.Acknowledged))
            .ToList();

        var summary = ConEngine.Dns.DnsSummary.Compute(engine.DnsWatch, engine.DnsResults);
        StatusResolver Resolver(TargetMonitor m) => new(m.Target.Id, m.Target.Address, m.Target.Name,
            m.CurrentState.ToString(), ResolverDetail(m.CurrentState, engine.DnsResults.Recent(m.Target.Id)), m.Target.Enabled);
        // A seeded DnsDown verdict has no blame until the first real classification.
        var blame = engine.Verdict?.Current.Dns is { } b ? new StatusDnsBlame(b.Kind.ToString(), b.Message, b.Resolvers) : null;
        var allRecent = engine.DnsWatch.System.Concat(engine.DnsWatch.Upstream)
            .SelectMany(m => engine.DnsResults.Recent(m.Target.Id)).OrderBy(r => r.At).ToList();
        var names = lookupNames.Select(n =>
        {
            var last = allRecent.LastOrDefault(r => string.Equals(r.Name, n.Name, StringComparison.OrdinalIgnoreCase));
            var result = last is null ? "none" : last.Outcome == "Filtered" ? "filtered" : last.Success ? "ok" : "miss";
            return new StatusLookupName(n.Name, result, last?.Message);
        }).ToList();
        var dns = new StatusDns(summary.Watched, summary.State.ToString(),
            engine.DnsWatch.System.Select(Resolver).ToList(), engine.DnsWatch.Upstream.Select(Resolver).ToList(), blame, names);

        return new StatusSnapshot(true, verdict, targets, pools, notes, dns);
    }

    /// <summary>The detail beside a resolver that isn't answering: its most recent failed
    /// DNS result, so a Down row never reads "Verified" because one later probe got through
    /// while the state still lags a burst; the newest message when nothing failed; null while
    /// answering or without samples (spec 2026-10-10 §5.2).</summary>
    public static string? ResolverDetail(HostState state, IReadOnlyList<ConEngine.Dns.DnsResult> recent)
    {
        if (recent.Count == 0 || TopologyCoordinator.Answering(state))
            return null;
        for (var i = recent.Count - 1; i >= 0; i--)
            if (!recent[i].Success)
                return recent[i].Message;
        return recent[^1].Message;
    }
}
