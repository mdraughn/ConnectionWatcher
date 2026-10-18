using Xunit.Sdk;

namespace ConEngine.Tests.Fakes;

/// <summary>Waits for a condition with a generous limit instead of sleeping and hoping
/// (CI runners have two cores). Fails with <c>what</c> when the limit passes.</summary>
public static class TestWait
{
    public static async Task Eventually(Func<bool> cond, int timeoutMs = 15000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (cond())
                return;
            await Task.Delay(25);
        }
        throw new XunitException(what ?? "Condition not met within timeout");
    }

    public static async Task Eventually(Func<Task<bool>> cond, int timeoutMs = 30000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await cond())
                return;
            await Task.Delay(50);
        }
        throw new XunitException(what ?? "Condition not met within timeout");
    }
}
