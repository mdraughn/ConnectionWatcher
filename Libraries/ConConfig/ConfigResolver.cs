namespace ConConfig;

/// <summary>
/// Precedence rules. Service values: flag &gt; environment &gt; machine file
/// &gt; default. Tray server URL: user file &gt; machine serverUrl &gt; machine
/// listenUrl rewritten to localhost &gt; built-in fallback.
/// </summary>
public static class ConfigResolver
{
    public const string DefaultListenUrl = "http://0.0.0.0:8080";
    public const string FallbackServerUrl = "http://localhost:8080";
    public const string EnvListenUrl = "CONWATCH_URLS";
    public const string EnvDataDir = "CONWATCH_DATADIR";

    public static Resolved<string> ListenUrl(ConfigPaths paths, ConWatchConfig? machine, string? flag, string? env) =>
        Pick(paths, flag, env, machine?.ListenUrl, DefaultListenUrl);

    public static Resolved<string> DataDir(ConfigPaths paths, ConWatchConfig? machine, string? flag, string? env) =>
        Pick(paths, flag, env, machine?.DataDir, paths.DefaultDataDir);

    public static Resolved<string> ServerUrl(ConfigPaths paths, ConWatchConfig? user, ConWatchConfig? machine)
    {
        if (user?.ServerUrl is { Length: > 0 } u)
            return new(u.TrimEnd('/'), ConfigSource.User, paths.UserFile);
        if (machine?.ServerUrl is { Length: > 0 } m)
            return new(m.TrimEnd('/'), ConfigSource.Machine, paths.MachineFile);
        if (machine?.ListenUrl is { Length: > 0 } l)
            return new(ListenToServerUrl(l), ConfigSource.Machine, paths.MachineFile);
        return new(FallbackServerUrl, ConfigSource.Default);
    }

    public static bool Notifications(ConWatchConfig? user) => user?.Notifications ?? true;

    /// <summary>0.0.0.0, +, *, [::] → localhost; trailing slash removed.</summary>
    public static string ListenToServerUrl(string listenUrl)
    {
        var trimmed = listenUrl.Trim().TrimEnd('/');
        foreach (var any in new[] { "0.0.0.0", "+", "*", "[::]" })
        {
            var marker = "://" + any;
            var at = trimmed.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
                return trimmed[..at] + "://localhost" + trimmed[(at + marker.Length)..];
        }
        return trimmed;
    }

    public static int PortOf(string listenUrl) => new Uri(ListenToServerUrl(listenUrl)).Port;

    public static bool ValidateUrl(string url, out string? error)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            error = null;
            return true;
        }
        error = $"'{url}' is not an absolute http or https URL.";
        return false;
    }

    public static bool ValidateDataDir(string dir, out string? error)
    {
        if (!Path.IsPathRooted(dir))
        {
            error = $"'{dir}' must be an absolute path.";
            return false;
        }
        try
        {
            Directory.CreateDirectory(dir);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"'{dir}' cannot be created: {ex.Message}";
            return false;
        }
    }

    private static Resolved<string> Pick(ConfigPaths paths, string? flag, string? env, string? machine, string fallback)
    {
        if (flag is { Length: > 0 }) return new(flag, ConfigSource.Flag);
        if (env is { Length: > 0 }) return new(env, ConfigSource.Environment);
        if (machine is { Length: > 0 }) return new(machine, ConfigSource.Machine, paths.MachineFile);
        return new(fallback, ConfigSource.Default);
    }
}
