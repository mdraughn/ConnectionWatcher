using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests.Fakes;

/// <summary>Counts timers, so a test can wait until a woken loop has armed its
/// next fake-time timer instead of sleeping and hoping: advancing before the
/// timer exists would skip right past it.</summary>
public sealed class TimerCountingTime : FakeTimeProvider
{
    private int _created;
    private int _active;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _created);
        Interlocked.Increment(ref _active);
        return new CountedTimer(base.CreateTimer(callback, state, dueTime, period), this);
    }

    /// <summary>Timers created and not yet disposed.</summary>
    public int ActiveTimers => Volatile.Read(ref _active);

    /// <summary>Waits until at least <paramref name="count"/> timers are active.</summary>
    public async Task WaitForTimersAsync(int count, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (ActiveTimers < count && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(ActiveTimers >= count, $"only {ActiveTimers} of {count} fake-time timers were armed");
    }

    public async Task NewTimerArmedSince(int before)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref _created) <= before && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(Volatile.Read(ref _created) > before, "the woken loop never armed a new timer");
    }

    public int TimersCreated => Volatile.Read(ref _created);

    private sealed class CountedTimer(ITimer inner, TimerCountingTime owner) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            Release();
            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Release();
            return inner.DisposeAsync();
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref owner._active);
        }
    }
}
