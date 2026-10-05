namespace ConInfrastructure.Discovery;

/// <summary>Best-effort "are we inside a container?" so searches can warn that the
/// default gateway may be the container bridge rather than the user's router.</summary>
public static class ContainerHint
{
    public const string Warning =
        "This server appears to be running in a container, so the default gateway it sees may be the " +
        "container's bridge rather than your router. Check the address before adopting it.";

    public static bool IsLikelyContainer() => IsLikelyContainer(Environment.GetEnvironmentVariable, File.Exists);

    public static bool IsLikelyContainer(Func<string, string?> env, Func<string, bool> fileExists) =>
        string.Equals(env("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
        fileExists("/.dockerenv");
}
