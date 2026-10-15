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
    [InlineData("Answered (dns.google)", "Answered", "dns.google")]
    [InlineData("Filtered: NXDOMAIN (dns.google)", "Filtered", "dns.google")]
    [InlineData("WrongAnswer: 10.0.0.1, 10.0.0.2 (one.one.one.one)", "WrongAnswer", "one.one.one.one")]
    [InlineData("TimedOut (example.org)", "TimedOut", "example.org")]
    [InlineData("NoLookupNames", "NoLookupNames", null)]
    [InlineData("ConnectionReset (dns.quad9.net)", "ConnectionReset", "dns.quad9.net")]
    public void Outcome_and_name_are_parsed_from_the_message(string message, string outcome, string? name)
    {
        var (parsedOutcome, parsedName) = DnsResultLog.Parse(message);
        Assert.Equal(outcome, parsedOutcome);
        Assert.Equal(name, parsedName);
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
