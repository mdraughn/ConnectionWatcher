using ConEngine.Topology;
using ConEngine.Tracking;

namespace ConEngine.Tests;

public class StateGroupTests
{
    [Theory]
    //          state                    answering undetermined failed healthy
    [InlineData(HostState.Initializing, false,    true,        false, false)]
    [InlineData(HostState.Starting,     false,    true,        false, true)]
    [InlineData(HostState.Responding,   true,     false,       false, true)]
    [InlineData(HostState.Faltering,    true,     false,       false, false)]
    [InlineData(HostState.Down,         false,    false,       true,  false)]
    [InlineData(HostState.Recovering,   true,     false,       false, false)]
    [InlineData(HostState.Unreachable,  false,    false,       true,  false)]
    public void Each_host_state_is_in_its_documented_groups(HostState s, bool answering, bool undetermined, bool failed, bool healthy)
    {
        Assert.Equal(answering, s.IsAnswering());
        Assert.Equal(undetermined, s.IsUndetermined());
        Assert.Equal(failed, s.IsFailed());
        Assert.Equal(healthy, s.IsHealthy());
    }

    [Fact]
    public void The_group_theories_cover_every_state()
    {
        // A new enum member must get a row in the theories above and below.
        Assert.Equal(7, Enum.GetValues<HostState>().Length);
        Assert.Equal(7, Enum.GetValues<VerdictState>().Length);
    }

    [Theory]
    [InlineData(VerdictState.Unknown, false)]
    [InlineData(VerdictState.LinkDown, false)]
    [InlineData(VerdictState.Healthy, true)]
    [InlineData(VerdictState.SiteIncident, true)]
    [InlineData(VerdictState.InternetWeather, true)]
    [InlineData(VerdictState.LinkDegraded, true)]
    [InlineData(VerdictState.DnsDown, true)]
    public void Reachable_means_neither_LinkDown_nor_Unknown(VerdictState v, bool reachable) =>
        Assert.Equal(reachable, v.IsReachable());
}
