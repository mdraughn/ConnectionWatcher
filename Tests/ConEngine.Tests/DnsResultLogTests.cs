using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Dns;
using ConEngine.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class DnsResultLogTests
{
    [Fact]
    public void Keeps_the_last_twenty_per_target_oldest_first_and_parses_outcome_and_name()
    {
        var log = new DnsResultLog();
        var id = Guid.NewGuid();
        var ip = IPAddress.Loopback;
        Assert.Empty(log.Recent(id));

        for (var i = 0; i < 25; i++)
            log.Record(id, new PingResult(ip, i % 2 == 0, 5, $"Answered (n{i}.test)", DateTimeOffset.UnixEpoch.AddSeconds(i)));

        var recent = log.Recent(id);
        Assert.Equal(DnsResultLog.Keep, recent.Count);
        Assert.Equal("n5.test", recent[0].Name);
        Assert.Equal("Answered", recent[0].Outcome);
        Assert.Equal("n24.test", recent[^1].Name);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(24), recent[^1].At);
        Assert.True(recent[^1].Success);
        Assert.Empty(log.Recent(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("Answered (dns.google)", "Answered", "dns.google", "")]
    [InlineData("Verified (dns.google)", "Verified", "dns.google", "")]
    [InlineData("Filtered: NXDOMAIN (dns.google)", "Filtered", "dns.google", "NXDOMAIN")]
    [InlineData("WrongAnswer: 10.0.0.1, 10.0.0.2 (one.one.one.one)", "WrongAnswer", "one.one.one.one", "10.0.0.1, 10.0.0.2")]
    [InlineData("TimedOut (example.org)", "TimedOut", "example.org", "")]
    [InlineData("NoLookupNames", "NoLookupNames", null, "")]
    [InlineData("ConnectionReset (dns.quad9.net)", "ConnectionReset", "dns.quad9.net", "")]
    [InlineData("", "", null, "")]
    [InlineData(":", "", null, "")]
    [InlineData("x (", "x", null, "(")]
    [InlineData(")", ")", null, "")]
    [InlineData("a ()", "a", null, "")]
    public void Outcome_and_name_are_parsed_from_the_message(string message, string outcome, string? name, string detail)
    {
        var (parsedOutcome, parsedName, parsedDetail) = DnsResultLog.Parse(message);
        Assert.Equal(outcome, parsedOutcome);
        Assert.Equal(name, parsedName);
        Assert.Equal(detail, parsedDetail);
    }

    [Theory]
    [InlineData("Filtered: NXDOMAIN (dns.google)", "NXDOMAIN")]
    [InlineData("WrongAnswer: 10.99.0.1, 10.99.0.2 (dns.google)", "10.99.0.1, 10.99.0.2")]
    [InlineData("WrongAnswer: 10.99.0.1", "10.99.0.1")]
    [InlineData("Filtered (dns.google)", "")]
    [InlineData("WrongAnswer", "")]
    public void Detail_is_the_text_between_the_outcome_and_the_name(string message, string detail) =>
        Assert.Equal(detail, DnsResultLog.Parse(message).Detail);

    [Fact]
    public void A_group_that_does_not_end_the_message_is_not_a_name()
    {
        var (outcome, name, detail) = DnsResultLog.Parse("WrongAnswer: 10.0.0.1 (x) more");
        Assert.Equal("WrongAnswer", outcome);
        Assert.Null(name);
        Assert.Equal("10.0.0.1 (x) more", detail);
    }

    [Fact]
    public void Record_keeps_the_detail()
    {
        var log = new DnsResultLog();
        var id = Guid.NewGuid();
        log.Record(id, new PingResult(IPAddress.Loopback, true, 5, "Filtered: NXDOMAIN (dns.google)", DateTimeOffset.UnixEpoch));
        Assert.Equal("NXDOMAIN", log.Recent(id)[0].Detail);
    }

    [Fact]
    public async Task The_engine_logs_Resolver_samples_and_nothing_else()
    {
        var options = new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
                new TargetConfig
                {
                    Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Internal",
                    Probe = "dns", Scope = TargetScope.Resolver,
                },
            ],
        };
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var resolverId = engine.Graph.GetByName("Your resolver 203.0.113.1").Id;
            var routerId = engine.Graph.GetByName("Router").Id;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (engine.DnsResults.Recent(resolverId).Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.NotEmpty(engine.DnsResults.Recent(resolverId));
            Assert.Empty(engine.DnsResults.Recent(routerId));
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
