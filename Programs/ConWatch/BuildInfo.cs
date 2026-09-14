using System.Reflection;

namespace ConWatch;

/// <summary>
/// Identifies this build so a glance (header) or a curl (/api/v1/health) can
/// answer "am I running the latest update?". The build time is the binary's
/// own file time, which survives publish, docker COPY, and the installer —
/// no build-system stamping required.
/// </summary>
public static class BuildInfo
{
    public static readonly string Version = ComputeVersion();

    /// <summary>Version without build metadata ("0.9.0") — what the UI shows;
    /// the full version with commit hash belongs in tooltips and APIs.</summary>
    public static readonly string ShortVersion = Version.Split('+')[0];

    public static readonly DateTimeOffset? BuiltUtc = ComputeBuiltUtc();

    private static string ComputeVersion()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
            return typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        // "1.2.3+<full commit sha>" reads better as "1.2.3+abcd1234".
        var plus = informational.IndexOf('+');
        return plus >= 0 && informational.Length > plus + 9
            ? informational[..(plus + 9)]
            : informational;
    }

    private static DateTimeOffset? ComputeBuiltUtc()
    {
        // Single-file publish leaves Assembly.Location empty; fall back to the exe.
        var path = typeof(BuildInfo).Assembly.Location;
        if (string.IsNullOrEmpty(path))
            path = Environment.ProcessPath;
        return path is { Length: > 0 } && File.Exists(path)
            ? File.GetLastWriteTimeUtc(path)
            : null;
    }
}
