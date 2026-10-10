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
    /// <summary>Below this, loss is noise, never blame.</summary>
    public const double MinAttributableLoss = 0.05;

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

    /// <summary>The anchors' median loss in the window (usable anchors only), or null.</summary>
    public static double? TypicalLoss(LossWindow window, IEnumerable<Guid> anchors)
    {
        var losses = anchors
            .Select(id => window.Targets.GetValueOrDefault(id))
            .Where(c => c is { Usable: true })
            .Select(c => c!.Loss)
            .Order()
            .ToList();
        if (losses.Count == 0)
            return null;
        int mid = losses.Count / 2;
        return losses.Count % 2 == 1 ? losses[mid] : (losses[mid - 1] + losses[mid]) / 2;
    }
}
