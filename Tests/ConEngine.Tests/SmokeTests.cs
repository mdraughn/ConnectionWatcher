using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class SmokeTests
{
    [Fact]
    public async Task FakeTimeProvider_drives_task_delay()
    {
        var time = new FakeTimeProvider();
        var delayTask = Task.Delay(TimeSpan.FromMinutes(5), time);

        Assert.False(delayTask.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(5));
        await delayTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(delayTask.IsCompletedSuccessfully);
    }
}
