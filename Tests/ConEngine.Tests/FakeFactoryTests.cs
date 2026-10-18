using System.Net;
using ConCore;
using ConEngine.Tests.Fakes;

namespace ConEngine.Tests;

public class FakeFactoryTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.0.2.9");

    private static async Task<bool[]> Ping(FakePingTransceiverFactory fake, int count)
    {
        using var t = fake.Create(Ip, ProbeSpec.Icmp, CancellationToken.None);
        var results = new bool[count];
        for (int i = 0; i < count; i++)
            results[i] = (await t.PingAsync(100)).Success;
        return results;
    }

    [Fact]
    public async Task Script_overrides_then_falls_back()
    {
        var fake = new FakePingTransceiverFactory();
        fake.Script(Ip, false, true);

        var results = await Ping(fake, 3);
        Assert.Equal(new[] { false, true, true }, results);
        Assert.Equal(3, fake.PingCount(Ip));
    }

    [Fact]
    public async Task DropEveryNth_drops_exactly_every_nth()
    {
        var fake = new FakePingTransceiverFactory();
        fake.DropEveryNth(Ip, 4);

        var results = await Ping(fake, 8);

        Assert.Equal(new[] { true, true, true, false, true, true, true, false }, results);
    }

    [Fact]
    public async Task Refused_looks_failed_but_reports_success()
    {
        var fake = new FakePingTransceiverFactory();
        fake.SetRefused(Ip, true);
        using var t = fake.Create(Ip, ProbeSpec.Icmp, CancellationToken.None);

        var r = await t.PingAsync(100);

        Assert.True(r.Success);
        Assert.Equal("Refused", r.Message);
    }
}
