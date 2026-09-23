namespace ConConfig;

/// <summary>
/// The well-known locations. Production uses <see cref="Default"/>; tests
/// build one over a temp directory.
/// </summary>
public sealed class ConfigPaths
{
    public const string Vendor = "DSoftCo";
    public const string Product = "ConnectionWatcher";

    public string MachineFile { get; }
    public string UserFile { get; }
    public string DefaultDataDir { get; }

    public ConfigPaths(string machineFile, string userFile, string defaultDataDir)
    {
        MachineFile = machineFile;
        UserFile = userFile;
        DefaultDataDir = defaultDataDir;
    }

    public static ConfigPaths Default()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var machineRoot = OperatingSystem.IsWindows()
            ? Path.Combine(programData, Vendor, Product)
            : Path.Combine("/etc", "conwatch");
        var dataDir = OperatingSystem.IsWindows()
            ? Path.Combine(programData, Vendor, Product, "data")
            : Path.GetFullPath("data");
        return new ConfigPaths(
            machineFile: Path.Combine(machineRoot, "conwatch.json"),
            userFile: Path.Combine(appData, Vendor, Product, "tray.json"),
            defaultDataDir: dataDir);
    }

    /// <summary>The pre-0.10 per-user tray file, read as a fallback by the tray.</summary>
    public string LegacyUserFile =>
        Path.Combine(Path.GetDirectoryName(UserFile)!, "contray-settings.json");

    /// <summary>All three under one temp root; the caller deletes the root.</summary>
    public static ConfigPaths UnderTemp(string root) => new(
        machineFile: Path.Combine(root, "machine", "conwatch.json"),
        userFile: Path.Combine(root, "user", "tray.json"),
        defaultDataDir: Path.Combine(root, "data"));
}
