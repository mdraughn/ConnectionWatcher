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
    public static ConWatchConfig? TryRead(string path, out string? error)
    {
        error = null;
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
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
}
