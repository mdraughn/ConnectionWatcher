using ConEngine.Topology;

namespace ConEngine.Tests;

public class LossAttributionTests
{
    private static readonly Guid Hop = Guid.NewGuid(), B1 = Guid.NewGuid(), B2 = Guid.NewGuid(), B3 = Guid.NewGuid();

    private static LossWindow W(params (Guid Id, int Samples, int Lost)[] counts) =>
        new(DateTimeOffset.Parse("2026-10-10T12:00:00Z"),
            counts.ToDictionary(c => c.Id, c => new WindowCount(c.Samples, c.Lost)));

    [Fact]
    public void Forwarding_loss_is_capped_by_the_lowest_loss_beyond()
    {
        // Corollary 3: the router "loses" 40 % of its own pings, but one target
        // beyond it lost nothing, so nothing was lost in forwarding.
        var loss = LossAttribution.ForHop(W((Hop, 10, 4), (B1, 10, 0), (B2, 10, 3)), Hop, [B1, B2])!;
        Assert.Equal(0.4, loss.Own, 3);
        Assert.Equal(0.0, loss.Forwarding, 3);
        Assert.Equal(0.4, loss.ProbeOnly, 3);
    }

    [Fact]
    public void Real_forwarding_loss_shows_at_the_hop_and_everything_beyond()
    {
        var loss = LossAttribution.ForHop(W((Hop, 10, 3), (B1, 10, 3), (B2, 20, 8)), Hop, [B1, B2])!;
        Assert.Equal(0.3, loss.Forwarding, 3);
        Assert.Equal(0.0, loss.ProbeOnly, 3);
    }

    [Fact]
    public void Forwarding_loss_never_exceeds_the_hops_own_loss()
    {
        var loss = LossAttribution.ForHop(W((Hop, 10, 1), (B1, 10, 5)), Hop, [B1])!;
        Assert.Equal(0.1, loss.Forwarding, 3);
    }

    [Fact]
    public void Without_enough_samples_or_a_witness_there_is_no_answer()
    {
        Assert.Null(LossAttribution.ForHop(W((Hop, 4, 0), (B1, 10, 0)), Hop, [B1]));   // hop not usable
        Assert.Null(LossAttribution.ForHop(W((Hop, 10, 0), (B1, 4, 0)), Hop, [B1]));   // witness not usable
        Assert.Null(LossAttribution.ForHop(W((Hop, 10, 0), (B1, 10, 10)), Hop, [B1])); // never answered
        Assert.Null(LossAttribution.ForHop(W((Hop, 10, 0)), Hop, [B1]));               // no samples beyond
    }
}
