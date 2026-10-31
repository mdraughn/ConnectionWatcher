using ConEngine.Scheduling;

namespace ConEngine.Tests.Fakes;

/// <summary>Simple constant-interval cadence (a test fake).</summary>
public class FixedIntervalCadence(TimeSpan interval, TimeProvider timeProvider) : IPingCadence
{
    public Task WaitForNextPingAsync(CancellationToken ct) =>
        Task.Delay(interval, timeProvider, ct);
}
