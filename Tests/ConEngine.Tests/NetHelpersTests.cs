using System.Net;
using System.Net.Sockets;
using ConInfrastructure.Net;

namespace ConEngine.Tests;

public class NetHelpersTests
{
    [Fact]
    public async Task A_cancelled_echo_throws_rather_than_reporting_no_answer()
    {
        using var cts = new CancellationTokenSource();
        var task = IcmpEcho.TryPingAsync(IPAddress.Parse("192.0.2.1"), TimeSpan.FromSeconds(10), cts.Token);
        cts.CancelAfter(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_lookup_slower_than_the_cap_is_null()
    {
        var never = new TaskCompletionSource<IPHostEntry>();
        Assert.Null(await ReverseDns.LookupAsync(IPAddress.Loopback, TimeSpan.FromMilliseconds(50), (_, _) => never.Task, CancellationToken.None));
    }

    [Fact]
    public async Task A_failing_lookup_is_null_but_cancellation_throws()
    {
        Assert.Null(await ReverseDns.LookupAsync(IPAddress.Loopback, TimeSpan.FromSeconds(5),
            (_, _) => Task.FromException<IPHostEntry>(new SocketException()), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReverseDns.LookupAsync(IPAddress.Loopback,
            TimeSpan.FromSeconds(5), (_, ct) => Task.FromCanceled<IPHostEntry>(ct), cts.Token));
    }

    [Theory]
    [InlineData("a:b:c:d:e:f", "0a:0b:0c:0d:0e:0f")]
    [InlineData("A4-BB-CC-DD-EE-FF", "a4:bb:cc:dd:ee:ff")]
    public void Macs_normalize_padded_and_lower_case(string text, string expected)
    {
        Assert.True(MacAddress.TryNormalize(text, out var mac));
        Assert.Equal(expected, mac);
    }
}
