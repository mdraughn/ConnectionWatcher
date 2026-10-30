using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConConfig;

public static class ConfigFile
{
    private static readonly string[] Known = ["listenUrl", "dataDir", "serverUrl", "notifications"];

    /// <summary>
    /// Missing → null with no error. Malformed → null with an error message
    /// that names the path. Never throws for I/O or parse problems.
    /// </summary>
    public static ConWatchConfig? TryRead(string path, out string? error) => TryRead(path, out error, out _);

    /// <summary>
    /// Same as the two-argument overload, but also reports whether the
    /// failure was <paramref name="unreadable"/> (the file could not be
    /// opened at all: <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>)
    /// as opposed to malformed (bad JSON/shape). Callers that must not
    /// silently treat "someone else has it open" as "reset to defaults"
    /// use <paramref name="unreadable"/> to tell the two apart.
    /// </summary>
    public static ConWatchConfig? TryRead(string path, out string? error, out bool unreadable)
    {
        error = null;
        unreadable = false;
        if (!File.Exists(path))
            return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new JsonException("top-level value is not an object");
            var extra = new Dictionary<string, JsonElement>();
            foreach (var (key, value) in node)
            {
                if (!Known.Contains(key))
                    extra[key] = JsonSerializer.SerializeToElement(value);
            }
            return new ConWatchConfig(
                ListenUrl: node["listenUrl"]?.GetValue<string>(),
                DataDir: node["dataDir"]?.GetValue<string>(),
                ServerUrl: node["serverUrl"]?.GetValue<string>(),
                Notifications: node["notifications"]?.GetValue<bool>())
            { Extra = extra };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {ex.Message}";
            unreadable = true;
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = $"{path}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Writes to a sibling temp file and moves it over the target, so a
    /// reader never sees a half-written file. Creates the directory. Nulls
    /// are omitted; <see cref="ConWatchConfig.Extra"/> is written back.
    /// </summary>
    public static void WriteAtomic(string path, ConWatchConfig config)
    {
        var node = new JsonObject();
        if (config.ListenUrl is not null) node["listenUrl"] = config.ListenUrl;
        if (config.DataDir is not null) node["dataDir"] = config.DataDir;
        if (config.ServerUrl is not null) node["serverUrl"] = config.ServerUrl;
        if (config.Notifications is not null) node["notifications"] = config.Notifications;
        foreach (var (key, value) in config.Extra)
            node[key] = JsonNode.Parse(value.GetRawText());

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Read-modify-write. <paramref name="change"/> gets the current config, or
    /// null when the file is missing or malformed. A malformed file is moved to
    /// <c>path + ".corrupt"</c> (after <paramref name="change"/> succeeds, so a
    /// rejected change touches nothing) and <paramref name="warning"/> says so.
    /// A file that cannot be opened at all throws <see cref="IOException"/>
    /// rather than being reset to defaults.
    /// </summary>
    public static ConWatchConfig Update(string path, Func<ConWatchConfig?, ConWatchConfig> change, out string? warning)
    {
        warning = null;
        var current = TryRead(path, out var error, out var unreadable);
        if (error is not null && unreadable)
            throw new IOException($"{path} could not be read: {error}");
        var next = change(current);
        if (error is not null)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            warning = $"{path} was not valid JSON; moved to {path}.corrupt and replaced";
        }
        WriteAtomic(path, next);
        return next;
    }
}
