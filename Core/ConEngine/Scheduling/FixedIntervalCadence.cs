namespace ConEngine.Scheduling;

/// <summary>Simple constant-interval cadence (used by tests and as a plain default).</summary>
public class FixedIntervalCadence(TimeSpan interval, TimeProvider timeProvider) : IPingCadence
{
    public Task WaitForNextPingAsync(CancellationToken ct) =>
        Task.Delay(interval, timeProvider, ct);
}
