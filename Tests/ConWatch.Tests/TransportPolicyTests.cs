using ConInfrastructure.Net;
using static ConInfrastructure.Net.TransportPolicy;

namespace ConWatch.Tests;

/// <summary>The requesting node's transport rule for sensitive cross-node
/// calls (spec 2026-09-12): https and loopback always pass; plain HTTP to a
/// LAN host passes only without sensitive material or with the override.</summary>
public class TransportPolicyTests
{
    [Theory]
    [InlineData("https://conwatch.example.com/", true, false, Verdict.Allowed)]
    [InlineData("https://conwatch.example.com/", true, true, Verdict.Allowed)]
    [InlineData("http://localhost:8080/", true, false, Verdict.Allowed)]
    [InlineData("http://127.0.0.1:8080/", true, false, Verdict.Allowed)]
    [InlineData("http://[::1]:8080/", true, false, Verdict.Allowed)]
    [InlineData("http://nas.example.com:8080/", false, false, Verdict.Allowed)] // sanitized is public
    [InlineData("http://nas.example.com:8080/", true, false, Verdict.RequiresOverride)]
    [InlineData("http://nas.example.com:8080/", true, true, Verdict.Allowed)]
    [InlineData("http://192.168.1.5:8080/", true, false, Verdict.RequiresOverride)]
    public void Evaluates_the_matrix(string url, bool sensitive, bool allowInsecure, Verdict expected)
        => Assert.Equal(expected, Evaluate(new Uri(url), sensitive, allowInsecure));
}
