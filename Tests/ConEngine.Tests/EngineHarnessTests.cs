using ConCore.Targets;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using ConEngine.Tracking;

namespace ConEngine.Tests;

public class EngineHarnessTests
{
    [Fact]
    public async Task Home_reaches_healthy_with_every_target_responding()
    {
        await using var h = EngineHarness.Home();
        await h.StartAsync();

        await h.WaitVerdictAsync(VerdictState.Healthy);

        foreach (var m in h.Engine.Monitors.Values.Where(m => m.Target.Scope == TargetScope.Verdict))
            await TestWait.Eventually(() => m.CurrentState == HostState.Responding,
                what: $"{m.Target.Name} never responded");
    }
}
