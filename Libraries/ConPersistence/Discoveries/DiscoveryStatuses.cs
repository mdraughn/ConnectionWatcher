using ConCore.Discovery;

namespace ConPersistence.Discoveries;

public static class DiscoveryStatuses
{
    /// <summary>"new" (also null/empty), "dismissed" (alias "hidden"), "adopted" or "all" (status = null), case-insensitive.</summary>
    public static bool TryParseFilter(string? text, out DiscoveryStatus? status)
    {
        status = DiscoveryStatus.New;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "new": return true;
            case "dismissed":
            case "hidden": status = DiscoveryStatus.Dismissed; return true;
            case "adopted": status = DiscoveryStatus.Adopted; return true;
            case "all": status = null; return true;
            default: status = null; return false;
        }
    }
}
