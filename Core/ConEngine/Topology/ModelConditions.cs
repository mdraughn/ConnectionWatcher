namespace ConEngine.Topology;

/// <summary>Ways ConnectionWatcher's map can disagree with what the probes prove (spec 2026-10-09 §4.6).</summary>
public enum ModelWarningKind
{
    /// <summary>A target is Down/Faltering while something beyond it answers: traffic passes it.</summary>
    HopBypassed,
    /// <summary>A target has never answered while something beyond it answers.</summary>
    NeverAnswered,
    /// <summary>Every anchor is the ISP's: "up" is proven only as far as the ISP.</summary>
    UnprovableUp,
    /// <summary>A hop keeps dropping its own probes while forwarding fine (spec §4.5, §4.6).</summary>
    ProbeOnlyLoss,
    /// <summary>Some, not all, system resolvers are Down while another answers.</summary>
    ResolverPartial,
    /// <summary>A resolver keeps returning wrong addresses for a seeded name.</summary>
    ResolverLying,
    /// <summary>A resolver blocks the seeded lookup names (a blocklist), so they can't prove resolution.</summary>
    ResolverFiltered,
    /// <summary>An upstream resolver is Down while a system resolver answers.</summary>
    UpstreamResolverDown,
    /// <summary>One lookup name misses on every answering resolver while other names hit.</summary>
    NameUnresolvable,
}

/// <summary>One condition that holds right now. Keyed so the same condition
/// on the same target is the same note across evaluations.</summary>
public sealed record ModelCondition(string Key, ModelWarningKind Kind, Guid? TargetId,
    string? TargetName, string Message)
{
    public static string KeyFor(ModelWarningKind kind, Guid? targetId)
    {
        var kindKey = kind switch
        {
            ModelWarningKind.HopBypassed => "hop-bypassed",
            ModelWarningKind.NeverAnswered => "never-answered",
            ModelWarningKind.UnprovableUp => "unprovable-up",
            ModelWarningKind.ProbeOnlyLoss => "probe-only-loss",
            ModelWarningKind.ResolverPartial => "resolver-partial",
            ModelWarningKind.ResolverLying => "resolver-lying",
            ModelWarningKind.ResolverFiltered => "resolver-filtered",
            ModelWarningKind.UpstreamResolverDown => "upstream-resolver-down",
            ModelWarningKind.NameUnresolvable => "name-unresolvable",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return targetId is { } id ? $"{kindKey}:{id:D}" : kindKey;
    }
}

/// <summary>A condition started (Active) or stopped holding.</summary>
public sealed record ModelConditionChange(ModelCondition Condition, bool Active, DateTimeOffset AtUtc);
