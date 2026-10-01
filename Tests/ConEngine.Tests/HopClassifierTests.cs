using ConInfrastructure.Traceroute;

namespace ConEngine.Tests;

public class HopClassifierTests
{
    private static TracerouteHop Hop(int ttl, string? address, bool reached = false) =>
        new(ttl, address, null, address is null ? null : ttl * 10, reached, null, null, null);

    private static IReadOnlyList<HopKind?> Kinds(params TracerouteHop[] hops) =>
        HopClassifier.Classify(hops).Select(h => h.Kind).ToList();

    [Fact]
    public void Lan_then_public_then_destination()
    {
        Assert.Equal([HopKind.Internal, HopKind.Internal, HopKind.Path, HopKind.Path, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, "192.168.100.1"), Hop(3, "96.120.0.1"),
                  Hop(4, "68.86.0.1"), Hop(5, "8.8.8.8", reached: true)));
    }

    [Fact]
    public void Cgnat_before_the_first_public_hop_is_internal()
    {
        Assert.Equal([HopKind.Internal, HopKind.Internal, HopKind.Path, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, "100.64.0.1"), Hop(3, "96.120.0.1"),
                  Hop(4, "1.1.1.1", reached: true)));
    }

    [Fact]
    public void Private_hop_after_a_public_one_is_path()
    {
        Assert.Equal([HopKind.Internal, HopKind.Path, HopKind.Path, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, "96.120.0.1"), Hop(3, "10.255.0.1"),
                  Hop(4, "1.1.1.1", reached: true)));
    }

    [Fact]
    public void Unanswered_hops_have_no_kind()
    {
        Assert.Equal([HopKind.Internal, null, HopKind.Path, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, null), Hop(3, "96.120.0.1"), Hop(4, "8.8.8.8", reached: true)));
    }

    [Fact]
    public void Private_destination_is_destination()
    {
        // Tracing to a LAN host: never "Internal", so the profile suggestion is Public,
        // which the user then corrects; and never an ASN candidate (Task 10).
        Assert.Equal([HopKind.Internal, HopKind.Destination],
            Kinds(Hop(1, "192.168.1.1"), Hop(2, "192.168.1.50", reached: true)));
    }

    [Fact]
    public void Other_fields_pass_through_unchanged()
    {
        var input = new TracerouteHop(3, "96.120.0.1", "ae1.example.net", 12, false, null, 7922, "COMCAST-7922");
        var output = HopClassifier.Classify([input]).Single();
        Assert.Equal(input with { Kind = HopKind.Path }, output);
    }
}
