using ConConfig;

namespace ConWatchCli.Services;

internal static class CliConfig
{
    /// <summary>
    /// <see cref="ConfigFile.Update"/> for commands: an unreadable file becomes a
    /// <see cref="ConfigException"/>, and the "malformed, kept as .corrupt" notice goes to stderr.
    /// </summary>
    public static ConWatchConfig Update(CliServices s, string path, Func<ConWatchConfig?, ConWatchConfig> change)
    {
        ConWatchConfig written;
        string? warning;
        try
        {
            written = ConfigFile.Update(path, change, out warning);
        }
        catch (IOException ex) when (ex.Message.StartsWith($"{path} could not be read: ", StringComparison.Ordinal))
        {
            throw new ConfigException(ex.Message);
        }
        if (warning is not null) s.Err.WriteLine($"warning: {warning}");
        return written;
    }
}
