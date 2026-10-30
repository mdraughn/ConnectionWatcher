using ConEngine.Dns;
using ConEngine.Tracking;

namespace ConWatch.Tests;

public class StatusResolverDetailTests
{
    private static DnsResult R(int minute, bool success, string message)
    {
        var (outcome, name, detail) = DnsResultLog.Parse(message);
        return new DnsResult(DateTimeOffset.Parse("2026-10-16T00:00:00Z").AddMinutes(minute), success, outcome, name, message) { Detail = detail };
    }

    [Fact]
    public void A_down_resolver_shows_its_last_failure_even_when_the_newest_sample_succeeded()
    {
        var recent = new[] { R(0, false, "TimedOut (dns.google)"), R(1, false, "ServFail (one.one.one.one)"), R(2, true, "Verified (dns.google)") };
        Assert.Equal("ServFail (one.one.one.one)", StatusSnapshotBuilder.ResolverDetail(HostState.Down, recent));
    }

    [Fact]
    public void With_only_successes_the_newest_message_is_shown_and_an_answering_resolver_shows_none()
    {
        var recent = new[] { R(0, true, "Verified (dns.google)"), R(1, true, "Verified (dns.quad9.net)") };
        Assert.Equal("Verified (dns.quad9.net)", StatusSnapshotBuilder.ResolverDetail(HostState.Starting, recent));
        Assert.Null(StatusSnapshotBuilder.ResolverDetail(HostState.Recovering, recent));
        Assert.Null(StatusSnapshotBuilder.ResolverDetail(HostState.Responding, recent));
        Assert.Null(StatusSnapshotBuilder.ResolverDetail(HostState.Down, []));
    }
}
