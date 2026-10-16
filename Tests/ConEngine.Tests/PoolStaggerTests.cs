using ConCore.Targets;

namespace ConEngine.Tests;

public class PoolStaggerTests
{
    [Fact]
    public void A_target_in_two_pools_takes_its_slot_from_the_first_pool_by_name_whatever_the_order() // Review Focus 4
    {
        var shared = Guid.NewGuid();
        var alpha = new PoolDefinition { Id = Guid.NewGuid(), Name = "Alpha", MemberTargetIds = [Guid.NewGuid(), shared, Guid.NewGuid()] };
        var beta = new PoolDefinition { Id = Guid.NewGuid(), Name = "beta", MemberTargetIds = [shared, Guid.NewGuid()] };
        var ab = WatcherEngine.PoolPositions([alpha, beta]);
        var ba = WatcherEngine.PoolPositions([beta, alpha]);
        Assert.Equal((1, 3), ab[shared]);
        Assert.Equal(ab[shared], ba[shared]);
    }
}
