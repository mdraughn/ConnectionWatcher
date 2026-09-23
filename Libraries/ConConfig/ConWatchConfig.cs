namespace ConConfig;

/// <summary>
/// The on-disk shape of conwatch.json (machine) and tray.json (user). Every
/// member is optional; a missing member means "not set here". Unknown JSON
/// properties survive a read/write round trip via <see cref="Extra"/>.
/// </summary>
public sealed record ConWatchConfig(
    string? ListenUrl = null,
    string? DataDir = null,
    string? ServerUrl = null,
    bool? Notifications = null)
{
    /// <summary>Properties this version does not understand, kept verbatim.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement> Extra { get; init; }
        = new Dictionary<string, System.Text.Json.JsonElement>();
}

/// <summary>Where a resolved value came from, most specific first.</summary>
public enum ConfigSource { Flag, Environment, User, Machine, Default }

/// <summary>A resolved value with its provenance; <see cref="Path"/> is the file for User/Machine.</summary>
public sealed record Resolved<T>(T Value, ConfigSource Source, string? Path = null)
{
    public string Describe() => Source switch
    {
        ConfigSource.Flag => "flag",
        ConfigSource.Environment => "environment",
        ConfigSource.User => $"user: {Path}",
        ConfigSource.Machine => $"machine: {Path}",
        _ => "default",
    };
}
