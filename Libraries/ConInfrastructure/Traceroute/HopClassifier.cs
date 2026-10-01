using ConCore;

namespace ConInfrastructure.Traceroute;

/// <summary>
/// Labels each answering hop. Private, link-local, loopback and CGNAT
/// addresses BEFORE the first public hop are the user's side (Internal);
/// everything else before the destination is on the route (Path), including
/// a private address that shows up inside a carrier; the hop that reached the
/// destination is Destination whatever its address.
/// </summary>
public static class HopClassifier
{
    public static IReadOnlyList<TracerouteHop> Classify(IReadOnlyList<TracerouteHop> hops)
    {
        var seenPublic = false;
        var result = new List<TracerouteHop>(hops.Count);
        foreach (var hop in hops)
        {
            if (hop.Address is null)
            {
                result.Add(hop with { Kind = null });
                continue;
            }
            HopKind kind;
            if (hop.ReachedDestination)
                kind = HopKind.Destination;
            else if (!seenPublic && ProbePolicy.IsPrivateOrCarrier(hop.Address))
                kind = HopKind.Internal;
            else
                kind = HopKind.Path;
            if (!ProbePolicy.IsPrivateOrCarrier(hop.Address))
                seenPublic = true;
            result.Add(hop with { Kind = kind });
        }
        return result;
    }
}
