namespace ConCore.Targets;

/// <summary>The one rule for which anchors prove the internet is up, and how operator tags compare.</summary>
public static class IspRule
{
    /// <summary>The comparison key for an operator tag: trimmed, lower-cased; null when blank.</summary>
    public static string? OperatorKey(string? tag) =>
        tag?.Trim() is { Length: > 0 } t ? t.ToLowerInvariant() : null;

    /// <summary>The anchors whose answer proves the internet is up: those whose operator is not on the
    /// trunk (the ISP is whoever operates the trunk; untagged anchors are never the ISP's; with no tag on
    /// the trunk every anchor counts). When every anchor is the ISP's, all of them count and
    /// <paramref name="usingIspFallback"/> is true (spec 2026-10-09 §4.1 decision (a)); false with no anchors.</summary>
    public static List<T> CountedAnchors<T>(IReadOnlyList<T> anchors, IEnumerable<T> trunk,
        Func<T, string?> operatorOf, out bool usingIspFallback)
    {
        var isp = trunk.Select(t => OperatorKey(operatorOf(t))).OfType<string>().ToHashSet();
        var counted = anchors.Where(a => OperatorKey(operatorOf(a)) is not { } key || !isp.Contains(key)).ToList();
        usingIspFallback = counted.Count == 0 && anchors.Count > 0;
        return usingIspFallback ? anchors.ToList() : counted;
    }
}
