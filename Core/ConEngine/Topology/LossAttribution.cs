namespace ConEngine.Topology;

/// <summary>
/// Spec 2026-10-09 §4.5, corollary 3: real forwarding loss at a hop shows up
/// at everything beyond it, so a hop's forwarding loss cannot exceed the
/// lowest loss seen beyond it. Loss above that figure belongs to the hop's
/// own probe (rate limiting, a busy control plane), not to the path. Pure;
/// never changes any state.
/// </summary>
public static class LossAttribution
{
    /// <param name="Own">The hop's own probe loss in the window.</param>
    /// <param name="Forwarding">The part of it that is real forwarding loss.</param>
    public sealed record HopLoss(double Own, double Forwarding)
    {
        /// <summary>Loss of the hop's own probe only.</summary>
        public double ProbeOnly => Own - Forwarding;
    }

    /// <summary>Null when the hop has too few samples, or nothing beyond it is a
    /// witness (usable and answered at least once).</summary>
    public static HopLoss? ForHop(LossWindow window, Guid hop, IEnumerable<Guid> beyond)
    {
        if (!window.Targets.TryGetValue(hop, out var own) || !own.Usable)
            return null;
        var witnesses = beyond
            .Select(id => window.Targets.GetValueOrDefault(id))
            .Where(c => c is { Usable: true, Answered: true })
            .Select(c => c!.Loss)
            .ToList();
        if (witnesses.Count == 0)
            return null;
        return new HopLoss(own.Loss, Math.Min(own.Loss, witnesses.Min()));
    }

    /// <summary>One window holding the summed counts of each target across
    /// <paramref name="windows"/> (oldest first; it starts where the first does).</summary>
    public static LossWindow Pool(IReadOnlyList<LossWindow> windows)
    {
        if (windows.Count == 0)
            throw new ArgumentException("Nothing to pool.", nameof(windows));
        var sums = new Dictionary<Guid, WindowCount>();
        foreach (var (id, count) in windows.SelectMany(w => w.Targets))
        {
            var prior = sums.GetValueOrDefault(id) ?? new WindowCount(0, 0);
            sums[id] = new WindowCount(prior.Samples + count.Samples, prior.Lost + count.Lost);
        }
        return new LossWindow(windows[0].Start, sums);
    }
}
