using ConCore.Targets;
using ConEngine.Scheduling;

namespace ConEngine.Tests;

public class PoolStaggerTests
{
    [Fact]
    public void A_target_in_two_pools_takes_its_slot_from_the_first_pool_by_name_whatever_the_order() // Review Focus 4
    {
        var shared = Guid.NewGuid();
        var alpha = new PoolDefinition { Id = Guid.NewGuid(), Name = "Alpha", MemberTargetIds = [Guid.NewGuid(), shared, Guid.NewGuid()] };
        var beta = new PoolDefinition { Id = Guid.NewGuid(), Name = "beta", MemberTargetIds = [shared, Guid.NewGuid()] };
        var ab = PoolStagger.Positions([alpha, beta]);
        var ba = PoolStagger.Positions([beta, alpha]);
        Assert.Equal((1, 3), ab[shared]);
        Assert.Equal(ab[shared], ba[shared]);
    }

    [Fact]
    public void Phase_offset_spreads_members_across_the_interval()
    {
        var interval = TimeSpan.FromSeconds(15);
        Assert.Equal(TimeSpan.Zero, PoolStagger.PhaseOffset(0, 3, interval));
        Assert.Equal(TimeSpan.FromSeconds(5), PoolStagger.PhaseOffset(1, 3, interval));
        Assert.Equal(TimeSpan.FromSeconds(10), PoolStagger.PhaseOffset(2, 3, interval));
    }
}
