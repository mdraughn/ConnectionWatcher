using ConCore;

namespace ConEngine.Tests;

public class ProbePolicyTests
{
    [Fact]
    public void Candidate_ports_follow_the_politeness_policy()
    {
        Assert.Equal([80, 443, 8080, 8443], ProbePolicy.CandidatePorts("192.168.1.1", null));
        Assert.Equal([80, 443, 8080, 8443, 22], ProbePolicy.CandidatePorts("192.168.1.1", 22));
        Assert.Equal([80, 443, 53], ProbePolicy.CandidatePorts("8.8.8.8", 8080));
        Assert.Equal([80, 443, 53], ProbePolicy.CandidatePorts("8.8.8.8", 443));
    }
}
