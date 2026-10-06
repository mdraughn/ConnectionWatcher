namespace ConCore.Discovery;

/// <summary>Limits shared by the store, the API and every client that posts discoveries.</summary>
public static class DiscoveryLimits
{
    /// <summary>The most discoveries one request or store call may carry.</summary>
    public const int MaxBatch = 512;
}
