# Single `conwatch` command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `ConWatch.exe` + `ConTray.exe` + PowerShell glue with one program folder holding `conwatch.exe` (console: help, serve, service, config, tray) and `conwatch-tray.exe` (GUI), one machine config file with a per-user override, a thinned Inno wizard, and a zip.

**Architecture:** `Programs/ConWatch` stays the cross-platform web host, its startup lifted into `ConWatchApp.RunAsync`. A new tiny library `Libraries/ConConfig` owns the config model, file I/O and precedence. `Programs/ConWatchCli` (System.CommandLine, `net10.0`, Web SDK so static assets and Razor application parts flow through) hosts the grammar over injectable seams (`IServiceManager`, `IElevation`, `IHealthProbe`, `IPortProbe`, `ITrayLauncher`, `ITrayAutostart`) so every command is unit-tested against fakes; the Windows implementations live in one folder. `Programs/ConWatchTray` is the old tray with its settings loader swapped for `ConConfig`. Both exes publish self-contained into one staging folder.

**Tech Stack:** .NET 10 (SDK 10.0.401), System.CommandLine 2.0.12, System.ServiceProcess.ServiceController 10.0.11, Microsoft.Extensions.Hosting.Systemd 10.0.11, xUnit 2.9.3, Inno Setup 6, PowerShell 7.

**Spec:** `docs/superpowers/specs/2026-09-19-conwatch-cli-design.md`

## Global Constraints

- Never push to origin. Commit on the feature branch only.
- Assembly names are exactly `conwatch` (console, `net10.0`) and `conwatch-tray` (WinExe, `net10.0-windows`). The tray never references the CLI; the CLI never references Windows Forms.
- Config paths: machine `%ProgramData%\DSoftCo\ConnectionWatcher\conwatch.json`; user `%AppData%\DSoftCo\ConnectionWatcher\tray.json`; default data dir `%ProgramData%\DSoftCo\ConnectionWatcher\data` (Windows) or `./data` (elsewhere). Property names `listenUrl`, `dataDir`, `serverUrl`, `notifications`; unknown properties are preserved on write.
- Precedence: service values flag > `CONWATCH_*` env > machine file > default (`listenUrl` default `http://0.0.0.0:8080`). Tray `serverUrl`: user file > machine `serverUrl` > machine `listenUrl` rewritten (`0.0.0.0`, `+`, `*`, `[::]` → `localhost`, trailing slash stripped) > `http://localhost:8080`.
- Exit codes: 0 ok, 1 usage, 2 config invalid, 3 service or tray exe not installed, 4 service control failed (includes non-Windows), 5 elevation declined.
- Elevated commands: `service install|uninstall|start|stop`, `config set` without `--user`. Never elevated: `serve`, `service status`, `config show`, `config set --user`, `tray`, `tray install|uninstall`, `version`, help.
- Windows service name `ConnectionWatcher`, display name `ConnectionWatcher`, binary path `"<exe>" serve` with no other arguments; firewall rule name `ConnectionWatcher`; Run-key value name `ConnectionWatcherTray`; mutex name `ConnectionWatcherTray`.
- No test touches SCM, UAC, the firewall, the registry, or real `%ProgramData%`/`%AppData%` paths. Every file-system test uses a temp directory it creates and deletes.
- Existing `ConWatch.Tests` must stay green and unchanged in behavior. The Dockerfile, `deploy/docker/compose.yaml`, and `publish-image.yml` are not modified.
- Commit messages: conventional prefixes (`feat:`, `refactor:`, `test:`, `build:`, `docs:`), no attribution lines.
- Run `dotnet test <project> -c Debug` from the repo root; the full suite is `dotnet test ConnectionWatcher.sln`.

---

## File structure

| Path | Responsibility |
|---|---|
| `Libraries/ConConfig/ConConfig.csproj` | `net10.0` class library, no packages |
| `Libraries/ConConfig/ConWatchConfig.cs` | the record + `ConfigSource` + `Resolved<T>` |
| `Libraries/ConConfig/ConfigPaths.cs` | the three well-known paths, overridable |
| `Libraries/ConConfig/ConfigFile.cs` | `TryRead`, `WriteAtomic` (preserves unknown properties) |
| `Libraries/ConConfig/ConfigResolver.cs` | precedence, `ListenToServerUrl`, `PortOf`, URL/dir validation |
| `Tests/ConConfig.Tests/*` | tests for the above |
| `Programs/ConWatch/ConWatchApp.cs` | `RunAsync(args, ct)` = today's `Program.cs` body |
| `Programs/ConWatch/Program.cs` | one line calling `ConWatchApp.RunAsync` + `partial class Program` |
| `Programs/ConWatchCli/ConWatchCli.csproj` | assembly `conwatch`, Sdk.Web, Exe |
| `Programs/ConWatchCli/Program.cs` | wires real implementations, handles `--__output` |
| `Programs/ConWatchCli/CliServices.cs` | the dependency record + `ExitCodes` |
| `Programs/ConWatchCli/CliApp.cs` | root command assembly, `RunAsync` |
| `Programs/ConWatchCli/Commands/ServeCommand.cs` | `serve` |
| `Programs/ConWatchCli/Commands/ServiceCommands.cs` | `service install/uninstall/start/stop/status` |
| `Programs/ConWatchCli/Commands/ConfigCommands.cs` | `config show/set` |
| `Programs/ConWatchCli/Commands/TrayCommands.cs` | `tray`, `tray install/uninstall` |
| `Programs/ConWatchCli/Commands/VersionCommand.cs` | `version` |
| `Programs/ConWatchCli/Services/Seams.cs` | `IServiceManager`, `ServiceSpec`, `ServiceStatus`, `IElevation`, `IHealthProbe`, `IPortProbe`, `ITrayLauncher`, `ITrayAutostart`, `ServiceControlException` |
| `Programs/ConWatchCli/Services/LegacyInstall.cs` | migration detection |
| `Programs/ConWatchCli/Services/HttpHealthProbe.cs` | `/api/v1/health` probe |
| `Programs/ConWatchCli/Services/TcpPortProbe.cs` | free-port probe |
| `Programs/ConWatchCli/Services/UnsupportedServiceManager.cs` | non-Windows |
| `Programs/ConWatchCli/Windows/WindowsServiceManager.cs` | sc.exe, netsh, Start Menu shortcut |
| `Programs/ConWatchCli/Windows/WindowsElevation.cs` | runas relaunch with output capture |
| `Programs/ConWatchCli/Windows/WindowsTrayAutostart.cs` | HKCU Run key |
| `Programs/ConWatchCli/Services/ProcessTrayLauncher.cs` | starts `conwatch-tray.exe` |
| `Tests/ConWatchCli.Tests/*` | fakes + command tests + one in-process host smoke test |
| `Programs/ConWatchTray/*` | moved tray; `TraySettings.cs` rewritten over `ConConfig` |
| `Build-Installer.ps1`, `deploy/windows/installer.iss`, `deploy/windows/README.txt` | build, wizard, zip |
| `.github/workflows/release.yml`, `.github/workflows/ci.yml` | zip artifact; new test projects |
| `README.md`, `CONTRIBUTING.md` | docs (the upgrade note lives in the README and the release body) |

---
### Task 1: `ConConfig` library — model, paths, file I/O

**Files:**
- Create: `Libraries/ConConfig/ConConfig.csproj`, `Libraries/ConConfig/ConWatchConfig.cs`, `Libraries/ConConfig/ConfigPaths.cs`, `Libraries/ConConfig/ConfigFile.cs`
- Create: `Tests/ConConfig.Tests/ConConfig.Tests.csproj`, `Tests/ConConfig.Tests/ConfigFileTests.cs`
- Modify: `ConnectionWatcher.sln` (via `dotnet sln add`)

**Interfaces:**
- Produces: `ConConfig.ConWatchConfig`, `ConConfig.ConfigSource`, `ConConfig.Resolved<T>`, `ConConfig.ConfigPaths`, `ConConfig.ConfigFile.TryRead/WriteAtomic` exactly as written below. Tasks 2, 6, 7 consume them.

- [x] **Step 1: Create the projects and add them to the solution**

```powershell
dotnet new classlib -n ConConfig -o Libraries/ConConfig -f net10.0
Remove-Item Libraries/ConConfig/Class1.cs
dotnet new xunit -n ConConfig.Tests -o Tests/ConConfig.Tests -f net10.0
Remove-Item Tests/ConConfig.Tests/UnitTest1.cs
dotnet add Tests/ConConfig.Tests reference Libraries/ConConfig
dotnet sln add Libraries/ConConfig Tests/ConConfig.Tests
```

Replace `Libraries/ConConfig/ConConfig.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Version, Company and Product come from Directory.Build.props. -->
  </PropertyGroup>
</Project>
```

Replace `Tests/ConConfig.Tests/ConConfig.Tests.csproj` with the same package set `Tests/ConWatch.Tests/ConWatch.Tests.csproj` uses, minus the ASP.NET ones:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Libraries\ConConfig\ConConfig.csproj" />
  </ItemGroup>
</Project>
```

- [x] **Step 2: Write the model and paths (no test needed; pure data)**

`Libraries/ConConfig/ConWatchConfig.cs`:

```csharp
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
```

`Libraries/ConConfig/ConfigPaths.cs`:

```csharp
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
```

- [x] **Step 3: Write the failing tests for `ConfigFile`**

`Tests/ConConfig.Tests/ConfigFileTests.cs`:

```csharp
using System.Text.Json;
using ConConfig;

namespace ConConfig.Tests;

public sealed class ConfigFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("conconfig").FullName;
    private string File(string name) => Path.Combine(_root, name);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Missing_file_reads_as_null_without_error()
    {
        var config = ConfigFile.TryRead(File("nope.json"), out var error);
        Assert.Null(config);
        Assert.Null(error);
    }

    [Fact]
    public void Malformed_file_reads_as_null_with_error_naming_the_path()
    {
        var path = File("bad.json");
        System.IO.File.WriteAllText(path, "{ not json");
        var config = ConfigFile.TryRead(path, out var error);
        Assert.Null(config);
        Assert.Contains(path, error);
    }

    [Fact]
    public void Reads_known_properties_and_keeps_unknown_ones()
    {
        var path = File("conwatch.json");
        System.IO.File.WriteAllText(path,
            """{ "listenUrl": "http://0.0.0.0:9090", "dataDir": "D:\\cw", "serverUrl": "http://nas:9090", "notifications": false, "future": { "x": 1 } }""");
        var config = ConfigFile.TryRead(path, out var error)!;
        Assert.Null(error);
        Assert.Equal("http://0.0.0.0:9090", config.ListenUrl);
        Assert.Equal(@"D:\cw", config.DataDir);
        Assert.Equal("http://nas:9090", config.ServerUrl);
        Assert.False(config.Notifications);
        Assert.Equal(1, config.Extra["future"].GetProperty("x").GetInt32());
    }

    [Fact]
    public void Write_creates_the_directory_and_round_trips_including_unknown_properties()
    {
        var path = File(Path.Combine("sub", "conwatch.json"));
        var extra = new Dictionary<string, JsonElement>
        {
            ["future"] = JsonDocument.Parse("""{"x":1}""").RootElement.Clone(),
        };
        ConfigFile.WriteAtomic(path, new ConWatchConfig(ListenUrl: "http://0.0.0.0:8080") { Extra = extra });
        var back = ConfigFile.TryRead(path, out _)!;
        Assert.Equal("http://0.0.0.0:8080", back.ListenUrl);
        Assert.Null(back.DataDir);
        Assert.Equal(1, back.Extra["future"].GetProperty("x").GetInt32());
        Assert.DoesNotContain("dataDir", System.IO.File.ReadAllText(path)); // nulls are omitted
    }

    [Fact]
    public void Write_replaces_an_existing_file_and_leaves_no_temp_file()
    {
        var path = File("conwatch.json");
        ConfigFile.WriteAtomic(path, new ConWatchConfig(ServerUrl: "http://a"));
        ConfigFile.WriteAtomic(path, new ConWatchConfig(ServerUrl: "http://b"));
        Assert.Equal("http://b", ConfigFile.TryRead(path, out _)!.ServerUrl);
        Assert.Single(Directory.GetFiles(_root));
    }
}
```

- [x] **Step 4: Run the tests to verify they fail**

Run: `dotnet test Tests/ConConfig.Tests -c Debug`
Expected: build error, `ConfigFile` does not exist.

- [x] **Step 5: Implement `ConfigFile`**

`Libraries/ConConfig/ConfigFile.cs`:

```csharp
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
```

- [x] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Tests/ConConfig.Tests -c Debug`
Expected: 5 passed.

- [x] **Step 7: Commit**

```powershell
git add Libraries/ConConfig Tests/ConConfig.Tests ConnectionWatcher.sln
git commit -m "feat(config): ConConfig library - config model, well-known paths, atomic JSON file I/O"
```

---

### Task 2: `ConfigResolver` — precedence, URL rewrite, validation

**Files:**
- Create: `Libraries/ConConfig/ConfigResolver.cs`
- Create: `Tests/ConConfig.Tests/ConfigResolverTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces: `ConfigResolver.ListenUrl(paths, machine, flag, env)`, `DataDir(...)`, `ServerUrl(paths, user, machine)`, `Notifications(user)`, `ListenToServerUrl(string)`, `PortOf(string)`, `ValidateUrl(string, out string? error)`, `ValidateDataDir(string, out string? error)`, constants `DefaultListenUrl`, `FallbackServerUrl`, env names `EnvListenUrl = "CONWATCH_URLS"`, `EnvDataDir = "CONWATCH_DATADIR"`.

- [x] **Step 1: Write the failing tests**

`Tests/ConConfig.Tests/ConfigResolverTests.cs`:

```csharp
using ConConfig;

namespace ConConfig.Tests;

public sealed class ConfigResolverTests
{
    private static readonly ConfigPaths Paths = ConfigPaths.UnderTemp(@"C:\t");

    [Fact]
    public void Listen_url_precedence_is_flag_env_machine_default()
    {
        var machine = new ConWatchConfig(ListenUrl: "http://0.0.0.0:3");
        Assert.Equal(("http://0.0.0.0:1", ConfigSource.Flag),
            Pair(ConfigResolver.ListenUrl(Paths, machine, flag: "http://0.0.0.0:1", env: "http://0.0.0.0:2")));
        Assert.Equal(("http://0.0.0.0:2", ConfigSource.Environment),
            Pair(ConfigResolver.ListenUrl(Paths, machine, flag: null, env: "http://0.0.0.0:2")));
        var fromMachine = ConfigResolver.ListenUrl(Paths, machine, null, null);
        Assert.Equal(("http://0.0.0.0:3", ConfigSource.Machine), Pair(fromMachine));
        Assert.Equal(Paths.MachineFile, fromMachine.Path);
        Assert.Equal((ConfigResolver.DefaultListenUrl, ConfigSource.Default),
            Pair(ConfigResolver.ListenUrl(Paths, null, null, null)));
    }

    [Fact]
    public void Data_dir_defaults_to_the_paths_default()
    {
        var r = ConfigResolver.DataDir(Paths, null, null, null);
        Assert.Equal(Paths.DefaultDataDir, r.Value);
        Assert.Equal(ConfigSource.Default, r.Source);
        Assert.Equal(@"D:\x", ConfigResolver.DataDir(Paths, new ConWatchConfig(DataDir: @"D:\x"), null, null).Value);
    }

    [Fact]
    public void Server_url_precedence_is_user_machine_server_machine_listen_fallback()
    {
        var user = new ConWatchConfig(ServerUrl: "http://user:1/");
        var machine = new ConWatchConfig(ListenUrl: "http://0.0.0.0:3", ServerUrl: "http://nas:2");
        Assert.Equal(("http://user:1", ConfigSource.User), Pair(ConfigResolver.ServerUrl(Paths, user, machine)));
        Assert.Equal(("http://nas:2", ConfigSource.Machine), Pair(ConfigResolver.ServerUrl(Paths, null, machine)));
        Assert.Equal(("http://localhost:3", ConfigSource.Machine),
            Pair(ConfigResolver.ServerUrl(Paths, null, new ConWatchConfig(ListenUrl: "http://0.0.0.0:3"))));
        Assert.Equal((ConfigResolver.FallbackServerUrl, ConfigSource.Default), Pair(ConfigResolver.ServerUrl(Paths, null, null)));
    }

    [Fact]
    public void Notifications_default_true()
    {
        Assert.True(ConfigResolver.Notifications(null));
        Assert.True(ConfigResolver.Notifications(new ConWatchConfig()));
        Assert.False(ConfigResolver.Notifications(new ConWatchConfig(Notifications: false)));
    }

    [Theory]
    [InlineData("http://0.0.0.0:8080", "http://localhost:8080")]
    [InlineData("http://+:8080/", "http://localhost:8080")]
    [InlineData("http://*:8080", "http://localhost:8080")]
    [InlineData("http://[::]:8080", "http://localhost:8080")]
    [InlineData("https://nas.local:8443/", "https://nas.local:8443")]
    [InlineData("http://192.168.1.5:8080", "http://192.168.1.5:8080")]
    public void Listen_to_server_url_rewrites_wildcards_and_strips_the_slash(string listen, string expected) =>
        Assert.Equal(expected, ConfigResolver.ListenToServerUrl(listen));

    [Theory]
    [InlineData("http://0.0.0.0:8080", 8080)]
    [InlineData("http://+:9090/", 9090)]
    [InlineData("http://[::]:7000", 7000)]
    [InlineData("http://localhost", 80)]
    public void Port_of_reads_the_port(string listen, int port) =>
        Assert.Equal(port, ConfigResolver.PortOf(listen));

    [Theory]
    [InlineData("http://nas:8080", true)]
    [InlineData("https://nas:8443/", true)]
    [InlineData("nas:8080", false)]
    [InlineData("ftp://nas", false)]
    [InlineData("", false)]
    public void Validate_url_accepts_only_absolute_http_or_https(string url, bool ok)
    {
        Assert.Equal(ok, ConfigResolver.ValidateUrl(url, out var error));
        Assert.Equal(ok, error is null);
    }

    [Fact]
    public void Validate_data_dir_requires_an_absolute_creatable_path()
    {
        var root = Directory.CreateTempSubdirectory("conconfig").FullName;
        try
        {
            Assert.True(ConfigResolver.ValidateDataDir(Path.Combine(root, "new", "deeper"), out var e1));
            Assert.Null(e1);
            Assert.True(Directory.Exists(Path.Combine(root, "new", "deeper")));
            Assert.False(ConfigResolver.ValidateDataDir("relative/dir", out var e2));
            Assert.Contains("absolute", e2);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static (string, ConfigSource) Pair(Resolved<string> r) => (r.Value, r.Source);
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/ConConfig.Tests -c Debug`
Expected: build error, `ConfigResolver` does not exist.

- [x] **Step 3: Implement `ConfigResolver`**

`Libraries/ConConfig/ConfigResolver.cs`:

```csharp
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/ConConfig.Tests -c Debug`
Expected: all passed (5 + 8 theories/facts).

- [x] **Step 5: Commit**

```powershell
git add Libraries/ConConfig Tests/ConConfig.Tests
git commit -m "feat(config): ConfigResolver - precedence, wildcard listen-to-server rewrite, validation"
```

---

### Task 3: Lift `Program.cs` into `ConWatchApp.RunAsync`; add `AddSystemd`

**Files:**
- Create: `Programs/ConWatch/ConWatchApp.cs`
- Modify: `Programs/ConWatch/Program.cs` (whole file), `Programs/ConWatch/ConWatch.csproj` (one package)
- Test: existing `Tests/ConWatch.Tests` (unchanged) must pass.

**Interfaces:**
- Produces: `ConWatch.ConWatchApp.RunAsync(string[] args, bool pinContentRoot = false, CancellationToken ct = default) : Task<int>`. Task 5 consumes it.

- [x] **Step 1: Add the systemd package**

```powershell
dotnet add Programs/ConWatch package Microsoft.Extensions.Hosting.Systemd --version 10.0.11
```

- [x] **Step 2: Create `ConWatchApp.cs` with the body of today's `Program.cs`**

Move everything from `Programs/ConWatch/Program.cs` between the `using` lines and the final `public partial class Program { }` into the method below. Only the marked lines change.

```csharp
using ConPersistence;
using ConWatch.Api;
using Microsoft.EntityFrameworkCore;

namespace ConWatch;

/// <summary>
/// The web host's whole startup, callable from Program.Main (container,
/// tests) and from the conwatch CLI's `serve` command.
/// </summary>
public static class ConWatchApp
{
    /// <param name="args">Host configuration arguments (--urls, --Persistence:DbPath, ...).</param>
    /// <param name="pinContentRoot">True to use the exe folder as content root
    /// regardless of the working directory (the CLI always passes true; the SCM
    /// case is detected here as before).</param>
    public static async Task<int> RunAsync(string[] args, bool pinContentRoot = false, CancellationToken ct = default)
    {
        // Under the Windows Service Control Manager the working directory is
        // System32 — pin the content root to the exe folder so appsettings and the
        // default data/ directory resolve next to the app. Both helpers are inert
        // when running normally (console, container, tests).
        var isWindowsService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = isWindowsService || pinContentRoot ? AppContext.BaseDirectory : null,   // CHANGED
        });
        builder.Services.AddWindowsService(options => options.ServiceName = "ConnectionWatcher");
        builder.Services.AddSystemd();                                                                  // NEW

        // ... the ShutdownTimeout block, the AddConWatch* chain, app.Build(),
        // DatabaseStartup.PrepareAsync, middleware and endpoint maps: verbatim ...

        try
        {
            await app.RunAsync(ct);                                                                     // CHANGED (was app.Run())
            return 0;                                                                                   // NEW
        }
        catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
        {
            var urls = builder.Configuration["urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "(default)";
            app.Logger.LogCritical(
                "ConnectionWatcher could not start: the listen address {Urls} is already in use by another program. " +
                "Change the port (service: conwatch config set listen-url http://0.0.0.0:<port>, then conwatch service stop / start; " +   // CHANGED text
                "console: --urls) and start again.",
                urls);
            throw;
        }
    }
}
```

- [x] **Step 3: Replace `Program.cs`**

```csharp
return await ConWatch.ConWatchApp.RunAsync(args);

public partial class Program { }
```

- [x] **Step 4: Build and run the existing suite**

Run: `dotnet build Programs/ConWatch -c Debug` then `dotnet test Tests/ConWatch.Tests -c Debug`
Expected: 0 warnings; the same pass count as before this task (record it in the report).

- [x] **Step 5: Commit**

```powershell
git add Programs/ConWatch/ConWatchApp.cs Programs/ConWatch/Program.cs Programs/ConWatch/ConWatch.csproj
git commit -m "refactor(host): lift startup into ConWatchApp.RunAsync; AddSystemd beside AddWindowsService"
```

---
### Task 4: `ConWatchCli` scaffold — seams, `CliServices`, root command, `version`, `serve`, test harness

**Files:**
- Create: `Programs/ConWatchCli/ConWatchCli.csproj`, `Programs/ConWatchCli/Program.cs`, `Programs/ConWatchCli/CliServices.cs`, `Programs/ConWatchCli/CliApp.cs`, `Programs/ConWatchCli/Services/Seams.cs`, `Programs/ConWatchCli/Commands/VersionCommand.cs`, `Programs/ConWatchCli/Commands/ServeCommand.cs`
- Create: `Tests/ConWatchCli.Tests/ConWatchCli.Tests.csproj`, `Tests/ConWatchCli.Tests/Fakes.cs`, `Tests/ConWatchCli.Tests/CliHarness.cs`, `Tests/ConWatchCli.Tests/RootCommandTests.cs`, `Tests/ConWatchCli.Tests/ServeCommandTests.cs`, `Tests/ConWatchCli.Tests/HostSmokeTests.cs`
- Modify: `ConnectionWatcher.sln`

**Interfaces:**
- Consumes: `ConConfig.*` (Tasks 1–2), `ConWatch.ConWatchApp.RunAsync` (Task 3).
- Produces: everything in `Seams.cs`, `CliServices`, `ExitCodes`, `CliApp.RunAsync(string[] args, CliServices s, CancellationToken ct)`, `CliApp.Guarded`, `CliApp.Elevated`, and the test harness `CliHarness`. Tasks 5–8 add commands by adding one `static Command Build(CliServices s, string[] rawArgs)` per file and registering it in `CliApp.BuildRoot`.

- [x] **Step 1: Create the projects**

`Programs/ConWatchCli/ConWatchCli.csproj` (Web SDK on purpose: it makes the referenced host's static web assets and Razor application parts flow into this exe's build and publish):

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>conwatch</AssemblyName>
    <RootNamespace>ConWatchCli</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyTitle>ConnectionWatcher command</AssemblyTitle>
    <!-- Version, Company and Product come from Directory.Build.props. -->
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ConWatch\ConWatch.csproj" />
    <ProjectReference Include="..\..\Libraries\ConConfig\ConConfig.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.0.12" />
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.11" />
  </ItemGroup>

</Project>
```

`Tests/ConWatchCli.Tests/ConWatchCli.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Programs\ConWatchCli\ConWatchCli.csproj" />
  </ItemGroup>
</Project>
```

```powershell
dotnet sln add Programs/ConWatchCli Tests/ConWatchCli.Tests
```

- [x] **Step 2: Write the seams**

`Programs/ConWatchCli/Services/Seams.cs`:

```csharp
namespace ConWatchCli.Services;

public enum ServiceState { NotInstalled, Stopped, Running, Other }

/// <param name="BinaryPath">The registered command line, e.g. "\"C:\...\conwatch.exe\" serve".</param>
public sealed record ServiceStatus(ServiceState State, string? BinaryPath = null, bool AutoStart = false, string? Detail = null)
{
    public static readonly ServiceStatus NotInstalled = new(ServiceState.NotInstalled);
}

/// <param name="ExePath">Full path of conwatch.exe.</param>
/// <param name="ServerUrl">Goes into the service description and the Start Menu shortcut.</param>
/// <param name="Start">Start the service after installing.</param>
public sealed record ServiceSpec(string ExePath, string ListenUrl, string DataDir, string ServerUrl, bool Start);

/// <summary>Thrown by service managers for any SCM/firewall/shortcut failure → exit 4.</summary>
public sealed class ServiceControlException(string message) : Exception(message);

/// <summary>Thrown by commands for invalid config values → exit 2.</summary>
public sealed class ConfigException(string message) : Exception(message);

public interface IServiceManager
{
    /// <summary>Idempotent: an existing service is stopped, deleted and recreated.</summary>
    Task InstallAsync(ServiceSpec spec, CancellationToken ct);
    /// <summary>Removes service, firewall rule and shortcut; leaves data and config.</summary>
    Task UninstallAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<ServiceStatus> StatusAsync(CancellationToken ct);
}

public interface IElevation
{
    bool IsElevated { get; }
    /// <summary>Relaunches this exe elevated with <paramref name="args"/>, echoes its output, returns its exit code (5 if declined).</summary>
    Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct);
}

/// <param name="Status">The `status` word from /api/v1/health, or null on failure.</param>
/// <param name="Failure">"refused", "timed out", "HTTP 503", ... or null on success.</param>
public sealed record HealthResult(string? Status, string? Failure, long ElapsedMs);

public interface IHealthProbe
{
    Task<HealthResult> ProbeAsync(string serverUrl, CancellationToken ct);
}

public interface IPortProbe
{
    bool IsFree(int port);
}

public interface ITrayLauncher
{
    /// <summary>Starts the tray exe and returns; false when the exe is missing.</summary>
    bool Launch(string trayExePath);
}

public interface ITrayAutostart
{
    bool IsInstalled { get; }
    void Install(string trayExePath);
    void Uninstall();
}
```

`Programs/ConWatchCli/CliServices.cs`:

```csharp
using ConConfig;
using ConWatchCli.Services;

namespace ConWatchCli;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int ConfigInvalid = 2;
    public const int NotInstalled = 3;
    public const int ServiceControlFailed = 4;
    public const int ElevationDeclined = 5;
}

/// <summary>
/// Everything a command needs, so tests can run the real grammar over fakes.
/// </summary>
/// <param name="Serve">Runs the web host with host arguments; production binds ConWatchApp.RunAsync.</param>
/// <param name="Env">Environment lookup; production binds Environment.GetEnvironmentVariable.</param>
/// <param name="AppDir">Folder holding conwatch.exe and conwatch-tray.exe.</param>
public sealed record CliServices(
    ConfigPaths Paths,
    IServiceManager Services,
    IElevation Elevation,
    IHealthProbe Health,
    IPortProbe Ports,
    ITrayLauncher TrayLauncher,
    ITrayAutostart TrayAutostart,
    Func<string[], CancellationToken, Task<int>> Serve,
    Func<string, string?> Env,
    string AppDir,
    string CliExePath,
    TextWriter Out,
    TextWriter Err,
    string Version)
{
    public string TrayExePath => Path.Combine(AppDir, OperatingSystem.IsWindows() ? "conwatch-tray.exe" : "conwatch-tray");
}
```

- [x] **Step 3: Write the failing tests and the harness**

`Tests/ConWatchCli.Tests/Fakes.cs`:

```csharp
using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class FakeServiceManager : IServiceManager
{
    public List<string> Calls { get; } = [];
    public ServiceSpec? Installed { get; private set; }
    public ServiceStatus Status { get; set; } = ServiceStatus.NotInstalled;
    public Exception? Throw { get; set; }

    public Task InstallAsync(ServiceSpec spec, CancellationToken ct) { Record("install"); Installed = spec; return Task.CompletedTask; }
    public Task UninstallAsync(CancellationToken ct) { Record("uninstall"); return Task.CompletedTask; }
    public Task StartAsync(CancellationToken ct) { Record("start"); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { Record("stop"); return Task.CompletedTask; }
    public Task<ServiceStatus> StatusAsync(CancellationToken ct) { Calls.Add("status"); return Task.FromResult(Status); }
    private void Record(string call) { Calls.Add(call); if (Throw is not null) throw Throw; }
}

public sealed class FakeElevation(bool elevated) : IElevation
{
    public bool IsElevated => elevated;
    public string[]? RelaunchArgs { get; private set; }
    public int RelaunchCode { get; set; } = 0;
    public Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct) { RelaunchArgs = args; return Task.FromResult(RelaunchCode); }
}

public sealed class FakeHealthProbe : IHealthProbe
{
    public HealthResult Result { get; set; } = new("Healthy", null, 41);
    public string? ProbedUrl { get; private set; }
    public Task<HealthResult> ProbeAsync(string serverUrl, CancellationToken ct) { ProbedUrl = serverUrl; return Task.FromResult(Result); }
}

public sealed class FakePortProbe : IPortProbe
{
    public HashSet<int> Busy { get; } = [];
    public bool IsFree(int port) => !Busy.Contains(port);
}

public sealed class FakeTrayLauncher(bool present = true) : ITrayLauncher
{
    public string? Launched { get; private set; }
    public bool Launch(string trayExePath) { if (!present) return false; Launched = trayExePath; return true; }
}

public sealed class FakeTrayAutostart : ITrayAutostart
{
    public string? InstalledPath { get; private set; }
    public bool IsInstalled => InstalledPath is not null;
    public void Install(string trayExePath) => InstalledPath = trayExePath;
    public void Uninstall() => InstalledPath = null;
}
```

`Tests/ConWatchCli.Tests/CliHarness.cs`:

```csharp
using ConConfig;
using ConWatchCli;

namespace ConWatchCli.Tests;

/// <summary>Runs the real grammar over fakes and a temp config root.</summary>
public sealed class CliHarness : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("conwatchcli").FullName;
    public ConfigPaths Paths { get; }
    public FakeServiceManager Services { get; } = new();
    public FakeElevation Elevation { get; }
    public FakeHealthProbe Health { get; } = new();
    public FakePortProbe Ports { get; } = new();
    public FakeTrayLauncher TrayLauncher { get; set; } = new();
    public FakeTrayAutostart TrayAutostart { get; } = new();
    public Dictionary<string, string?> Env { get; } = new();
    public string[]? ServeArgs { get; private set; }
    public string AppDir => Path.Combine(Root, "app");

    public CliHarness(bool elevated = true)
    {
        Paths = ConfigPaths.UnderTemp(Root);
        Elevation = new FakeElevation(elevated);
        Directory.CreateDirectory(AppDir);
    }

    public async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var services = new CliServices(
            Paths, Services, Elevation, Health, Ports, TrayLauncher, TrayAutostart,
            Serve: (a, _) => { ServeArgs = a; return Task.FromResult(0); },
            Env: name => Env.GetValueOrDefault(name),
            AppDir: AppDir,
            CliExePath: Path.Combine(AppDir, "conwatch.exe"),
            Out: output, Err: error, Version: "9.9.9-test+abc");
        var code = await CliApp.RunAsync(args, services, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    public void WriteMachine(ConWatchConfig config) => ConfigFile.WriteAtomic(Paths.MachineFile, config);
    public void WriteUser(ConWatchConfig config) => ConfigFile.WriteAtomic(Paths.UserFile, config);
    public ConWatchConfig? Machine => ConfigFile.TryRead(Paths.MachineFile, out _);
    public ConWatchConfig? User => ConfigFile.TryRead(Paths.UserFile, out _);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
```

`Tests/ConWatchCli.Tests/RootCommandTests.cs`:

```csharp
namespace ConWatchCli.Tests;

public sealed class RootCommandTests
{
    [Fact]
    public async Task No_arguments_prints_help_and_returns_zero()
    {
        using var h = new CliHarness();
        var (code, output, _) = await h.RunAsync();
        Assert.Equal(0, code);
        Assert.Contains("serve", output);
        Assert.Contains("version", output);
        Assert.Contains("conwatch.json", output);
        // Task 8 extends this with "service", "config", "tray" once they exist.
    }

    [Fact]
    public async Task Unknown_command_is_a_usage_error()
    {
        using var h = new CliHarness();
        var (code, _, error) = await h.RunAsync("bogus");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task Version_prints_the_informational_version()
    {
        using var h = new CliHarness();
        var (code, output, _) = await h.RunAsync("version");
        Assert.Equal(0, code);
        Assert.Contains("9.9.9-test+abc", output);
    }
}
```

`Tests/ConWatchCli.Tests/ServeCommandTests.cs`:

```csharp
using ConConfig;

namespace ConWatchCli.Tests;

public sealed class ServeCommandTests
{
    [Fact]
    public async Task Serve_passes_defaults_as_host_arguments()
    {
        using var h = new CliHarness();
        var (code, _, _) = await h.RunAsync("serve");
        Assert.Equal(0, code);
        Assert.Equal(["--urls", ConfigResolver.DefaultListenUrl, "--Persistence:DbPath", Path.Combine(h.Paths.DefaultDataDir, "conwatch.db")], h.ServeArgs);
    }

    [Fact]
    public async Task Serve_reads_the_machine_file_and_flags_win()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:9000", DataDir: Path.Combine(h.Root, "d")));
        await h.RunAsync("serve");
        Assert.Equal("http://0.0.0.0:9000", h.ServeArgs![1]);
        Assert.Equal(Path.Combine(h.Root, "d", "conwatch.db"), h.ServeArgs[3]);

        await h.RunAsync("serve", "--url", "http://0.0.0.0:9001");
        Assert.Equal("http://0.0.0.0:9001", h.ServeArgs![1]);
    }

    [Fact]
    public async Task Serve_environment_beats_the_machine_file()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:9000"));
        h.Env[ConfigResolver.EnvListenUrl] = "http://0.0.0.0:9002";
        await h.RunAsync("serve");
        Assert.Equal("http://0.0.0.0:9002", h.ServeArgs![1]);
    }

    [Fact]
    public async Task Serve_never_elevates()
    {
        using var h = new CliHarness(elevated: false);
        await h.RunAsync("serve");
        Assert.Null(h.Elevation.RelaunchArgs);
        Assert.NotNull(h.ServeArgs);
    }
}
```

`Tests/ConWatchCli.Tests/HostSmokeTests.cs` (proves the Razor application part and the host wiring survive being hosted from a different entry assembly; this is the one test that boots the real host):

```csharp
using System.Net;
using System.Net.Sockets;
using ConWatch;

namespace ConWatchCli.Tests;

public sealed class HostSmokeTests
{
    [Fact]
    public async Task ConWatchApp_serves_health_and_the_dashboard_from_a_pinned_content_root()
    {
        var dir = Directory.CreateTempSubdirectory("conwatch-smoke").FullName;
        var port = FreePort();
        using var cts = new CancellationTokenSource();
        var run = ConWatchApp.RunAsync(
            ["--urls", $"http://127.0.0.1:{port}", "--Persistence:DbPath", Path.Combine(dir, "conwatch.db")],
            pinContentRoot: true, ct: cts.Token);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            HttpResponseMessage? health = null;
            for (var i = 0; i < 60 && health is null; i++)
            {
                try { health = await http.GetAsync("/api/v1/health"); }
                catch (HttpRequestException) { await Task.Delay(500); }
            }
            Assert.NotNull(health);
            Assert.Equal(HttpStatusCode.OK, health!.StatusCode);

            var home = await http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            Assert.Contains("ConnectionWatcher", await home.Content.ReadAsStringAsync());
        }
        finally
        {
            cts.Cancel();
            await run;
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
```

- [x] **Step 4: Run the tests to verify they fail**

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug`
Expected: build errors (`CliApp` missing).

- [x] **Step 5: Implement `CliApp`, `version`, `serve`, and `Program`**

`Programs/ConWatchCli/CliApp.cs`:

```csharp
using System.CommandLine;
using ConWatchCli.Commands;
using ConWatchCli.Services;

namespace ConWatchCli;

public static class CliApp
{
    public const string ServiceName = "ConnectionWatcher";

    public static RootCommand BuildRoot(CliServices s, string[] rawArgs)
    {
        var root = new RootCommand(
            "ConnectionWatcher: run the monitor, manage its Windows service, its config, and the tray companion.\n" +
            $"Machine config: {s.Paths.MachineFile}\nPer-user tray config: {s.Paths.UserFile}");
        root.Subcommands.Add(VersionCommand.Build(s));
        root.Subcommands.Add(ServeCommand.Build(s));
        // Tasks 5, 7, 8 add: ServiceCommands.Build(s, rawArgs), ConfigCommands.Build(s, rawArgs), TrayCommands.Build(s)
        return root;
    }

    public static async Task<int> RunAsync(string[] args, CliServices s, CancellationToken ct)
    {
        if (args.Length == 0)
            args = ["--help"];
        var root = BuildRoot(s, args);
        var parse = root.Parse(args);
        var config = new InvocationConfiguration { Output = s.Out, Error = s.Err };
        return await parse.InvokeAsync(config, ct);
    }

    /// <summary>Maps the typed exceptions to the exit-code table.</summary>
    public static async Task<int> Guarded(CliServices s, Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (ConfigException ex)
        {
            s.Err.WriteLine(ex.Message);
            return ExitCodes.ConfigInvalid;
        }
        catch (ServiceControlException ex)
        {
            s.Err.WriteLine(ex.Message);
            return ExitCodes.ServiceControlFailed;
        }
        catch (PlatformNotSupportedException ex)
        {
            s.Err.WriteLine(ex.Message);
            return ExitCodes.ServiceControlFailed;
        }
    }

    /// <summary>Runs <paramref name="body"/> inline when elevated, else relaunches with the original arguments.</summary>
    public static Task<int> Elevated(CliServices s, string[] rawArgs, Func<Task<int>> body, CancellationToken ct) =>
        s.Elevation.IsElevated ? Guarded(s, body) : s.Elevation.RelaunchElevatedAsync(rawArgs, ct);
}
```

`Programs/ConWatchCli/Commands/VersionCommand.cs`:

```csharp
using System.CommandLine;

namespace ConWatchCli.Commands;

public static class VersionCommand
{
    public static Command Build(CliServices s)
    {
        var cmd = new Command("version", "Print the version and build stamp.");
        cmd.SetAction(_ => { s.Out.WriteLine($"conwatch {s.Version}"); return ExitCodes.Ok; });
        return cmd;
    }
}
```

`Programs/ConWatchCli/Commands/ServeCommand.cs`:

```csharp
using System.CommandLine;
using ConConfig;

namespace ConWatchCli.Commands;

public static class ServeCommand
{
    public static Command Build(CliServices s)
    {
        var url = new Option<string?>("--url") { Description = "Listen URL (default from the machine config, else http://0.0.0.0:8080)." };
        var data = new Option<string?>("--data") { Description = "Data folder holding conwatch.db." };
        var cmd = new Command("serve", "Run the watcher: as a Windows service when started by the service manager, otherwise in the foreground (Ctrl+C stops).");
        cmd.Options.Add(url);
        cmd.Options.Add(data);
        cmd.SetAction((pr, ct) => CliApp.Guarded(s, () =>
        {
            var hostArgs = HostArguments(s, pr.GetValue(url), pr.GetValue(data));
            return s.Serve(hostArgs, ct);
        }));
        return cmd;
    }

    /// <summary>Resolves listen URL and data dir (flag > env > machine > default) into host arguments.</summary>
    public static string[] HostArguments(CliServices s, string? urlFlag, string? dataFlag)
    {
        var machine = ConfigFile.TryRead(s.Paths.MachineFile, out var warning);
        if (warning is not null)
            s.Err.WriteLine($"warning: ignoring malformed config {warning}");
        var listen = ConfigResolver.ListenUrl(s.Paths, machine, urlFlag, s.Env(ConfigResolver.EnvListenUrl));
        var dataDir = ConfigResolver.DataDir(s.Paths, machine, dataFlag, s.Env(ConfigResolver.EnvDataDir));
        return ["--urls", listen.Value, "--Persistence:DbPath", Path.Combine(dataDir.Value, "conwatch.db")];
    }
}
```

`Programs/ConWatchCli/Program.cs` (real implementations for the seams arrive in Tasks 6 and 8; until then the placeholders below compile and throw `PlatformNotSupportedException`, which `Guarded` maps to exit 4):

```csharp
using System.Reflection;
using ConConfig;
using ConWatchCli;
using ConWatchCli.Services;

// `--__output <file>`: written by the elevated relaunch so the parent can echo
// our output into the caller's terminal. Stripped before parsing.
var (args2, outputFile) = StripOutputOption(args);
TextWriter output = Console.Out, error = Console.Error;
StreamWriter? capture = null;
if (outputFile is not null)
{
    capture = new StreamWriter(outputFile) { AutoFlush = true };
    output = error = capture;
}

try
{
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    var services = new CliServices(
        Paths: ConfigPaths.Default(),
        Services: new UnsupportedServiceManager(),      // Task 6 swaps in WindowsServiceManager on Windows
        Elevation: new AlwaysElevated(),                // Task 6 swaps in WindowsElevation on Windows
        Health: new UnsupportedHealthProbe(),           // Task 6 swaps in HttpHealthProbe
        Ports: new UnsupportedPortProbe(),              // Task 6 swaps in TcpPortProbe
        TrayLauncher: new UnsupportedTrayLauncher(),    // Task 8
        TrayAutostart: new UnsupportedTrayAutostart(),  // Task 8
        Serve: (hostArgs, ct) => ConWatch.ConWatchApp.RunAsync(hostArgs, pinContentRoot: true, ct),
        Env: Environment.GetEnvironmentVariable,
        AppDir: appDir,
        CliExePath: Environment.ProcessPath ?? Path.Combine(appDir, "conwatch.exe"),
        Out: output, Err: error, Version: version);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    return await CliApp.RunAsync(args2, services, cts.Token);
}
finally
{
    capture?.Dispose();
}

static (string[] Args, string? OutputFile) StripOutputOption(string[] args)
{
    var at = Array.IndexOf(args, "--__output");
    if (at < 0 || at + 1 >= args.Length)
        return (args, null);
    return (args.Where((_, i) => i != at && i != at + 1).ToArray(), args[at + 1]);
}

public partial class Program { }

file sealed class AlwaysElevated : IElevation
{
    public bool IsElevated => true;
    public Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct) => Task.FromResult(ExitCodes.Ok);
}
file sealed class UnsupportedHealthProbe : IHealthProbe
{
    public Task<HealthResult> ProbeAsync(string serverUrl, CancellationToken ct) => Task.FromResult(new HealthResult(null, "not implemented", 0));
}
file sealed class UnsupportedPortProbe : IPortProbe { public bool IsFree(int port) => true; }
file sealed class UnsupportedTrayLauncher : ITrayLauncher { public bool Launch(string trayExePath) => false; }
file sealed class UnsupportedTrayAutostart : ITrayAutostart
{
    public bool IsInstalled => false;
    public void Install(string trayExePath) => throw new PlatformNotSupportedException("tray autostart is not implemented on this platform");
    public void Uninstall() => throw new PlatformNotSupportedException("tray autostart is not implemented on this platform");
}
```

`Programs/ConWatchCli/Services/UnsupportedServiceManager.cs`:

```csharp
namespace ConWatchCli.Services;

/// <summary>Every non-Windows OS until a systemd implementation exists.</summary>
public sealed class UnsupportedServiceManager : IServiceManager
{
    private const string Message = "service control is not implemented on this platform";
    public Task InstallAsync(ServiceSpec spec, CancellationToken ct) => throw new PlatformNotSupportedException(Message);
    public Task UninstallAsync(CancellationToken ct) => throw new PlatformNotSupportedException(Message);
    public Task StartAsync(CancellationToken ct) => throw new PlatformNotSupportedException(Message);
    public Task StopAsync(CancellationToken ct) => throw new PlatformNotSupportedException(Message);
    public Task<ServiceStatus> StatusAsync(CancellationToken ct) => throw new PlatformNotSupportedException(Message);
}
```

- [x] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug`
Expected: 8 passed (3 root, 4 serve, 1 smoke). If the smoke test's `/` returns 404, the Razor application part did not flow: confirm `ConWatchCli.csproj` uses `Microsoft.NET.Sdk.Web` and that `obj/.../conwatch.MvcApplicationPartsAssemblyInfo.cs` names `ConWatch`.

- [x] **Step 7: Run it for real once**

Run: `dotnet run --project Programs/ConWatchCli -- --help` and `dotnet run --project Programs/ConWatchCli -- version`
Expected: help lists `version` and `serve`; version prints `conwatch 0.9.0-dev+<sha>`.

- [x] **Step 8: Commit**

```powershell
git add Programs/ConWatchCli Tests/ConWatchCli.Tests ConnectionWatcher.sln
git commit -m "feat(cli): conwatch command scaffold - seams, help, version, serve; host smoke test"
```

---

### Task 5: `service install | uninstall | start | stop | status` with migration and port probe

**Files:**
- Create: `Programs/ConWatchCli/Commands/ServiceCommands.cs`, `Programs/ConWatchCli/Services/LegacyInstall.cs`
- Modify: `Programs/ConWatchCli/CliApp.cs` (register)
- Create: `Tests/ConWatchCli.Tests/ServiceCommandTests.cs`, `Tests/ConWatchCli.Tests/LegacyInstallTests.cs`

**Interfaces:**
- Consumes: Task 4 seams and harness.
- Produces: `ServiceCommands.Build(CliServices s, string[] rawArgs)`, `LegacyInstall.Detect(string appDir, string? serviceBinaryPath) : LegacyValues(string? ServerUrl, string? ListenUrl)`.

- [x] **Step 1: Write the failing tests**

`Tests/ConWatchCli.Tests/LegacyInstallTests.cs`:

```csharp
using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class LegacyInstallTests : IDisposable
{
    private readonly string _app = Directory.CreateTempSubdirectory("legacy").FullName;
    public void Dispose() => Directory.Delete(_app, recursive: true);

    [Fact]
    public void Nothing_legacy_yields_nulls()
    {
        var v = LegacyInstall.Detect(_app, null);
        Assert.Null(v.ServerUrl);
        Assert.Null(v.ListenUrl);
    }

    [Fact]
    public void Reads_contray_json_from_the_old_watcher_or_tray_folder()
    {
        Directory.CreateDirectory(Path.Combine(_app, "Tray"));
        File.WriteAllText(Path.Combine(_app, "Tray", "contray.json"), """{ "url": "http://nas:9090" }""");
        Assert.Equal("http://nas:9090", LegacyInstall.Detect(_app, null).ServerUrl);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\DSoftCo\\ConnectionWatcher\\Watcher\\ConWatch.exe\" --urls http://0.0.0.0:8081 --Persistence:DbPath \"C:\\x\\conwatch.db\"", "http://0.0.0.0:8081")]
    [InlineData("\"C:\\app\\conwatch.exe\" serve", null)]
    [InlineData("\"C:\\old\\ConWatch.exe\"", null)]
    public void Reads_the_listen_url_from_an_old_service_binary_path(string binaryPath, string? expected) =>
        Assert.Equal(expected, LegacyInstall.Detect(_app, binaryPath).ListenUrl);
}
```

`Tests/ConWatchCli.Tests/ServiceCommandTests.cs`:

```csharp
using ConConfig;
using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class ServiceCommandTests
{
    [Fact]
    public async Task Install_probes_a_free_port_writes_the_machine_file_and_installs()
    {
        using var h = new CliHarness();
        h.Ports.Busy.Add(8080);
        h.Ports.Busy.Add(8081);
        var (code, output, _) = await h.RunAsync("service", "install");
        Assert.Equal(0, code);
        var spec = h.Services.Installed!;
        Assert.Equal("http://0.0.0.0:8082", spec.ListenUrl);
        Assert.Equal(h.Paths.DefaultDataDir, spec.DataDir);
        Assert.Equal("http://localhost:8082", spec.ServerUrl);
        Assert.Equal(Path.Combine(h.AppDir, "conwatch.exe"), spec.ExePath);
        Assert.True(spec.Start);
        Assert.Equal("http://0.0.0.0:8082", h.Machine!.ListenUrl);
        Assert.Equal(h.Paths.DefaultDataDir, h.Machine.DataDir);
        Assert.Equal("http://localhost:8082", h.Machine.ServerUrl);
        Assert.Contains("http://localhost:8082", output);
    }

    [Fact]
    public async Task Install_with_port_pins_it_even_when_busy_and_no_start_is_honored()
    {
        using var h = new CliHarness();
        h.Ports.Busy.Add(9000);
        var (code, _, error) = await h.RunAsync("service", "install", "--port", "9000", "--no-start");
        Assert.Equal(0, code);
        Assert.Equal("http://0.0.0.0:9000", h.Services.Installed!.ListenUrl);
        Assert.False(h.Services.Installed.Start);
        Assert.Contains("in use", error);
    }

    [Fact]
    public async Task Install_keeps_an_existing_machine_file_port_and_server_url()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:7000", ServerUrl: "http://nas:7000"));
        h.Ports.Busy.Add(7000); // the old service is still holding it; that is fine
        await h.RunAsync("service", "install");
        Assert.Equal("http://0.0.0.0:7000", h.Services.Installed!.ListenUrl);
        Assert.Equal("http://nas:7000", h.Services.Installed.ServerUrl);
    }

    [Fact]
    public async Task Install_url_and_port_together_is_a_usage_error()
    {
        using var h = new CliHarness();
        var (code, _, _) = await h.RunAsync("service", "install", "--port", "1", "--url", "http://0.0.0.0:2");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(h.Services.Calls);
    }

    [Fact]
    public async Task Install_rejects_a_bad_url_with_config_invalid()
    {
        using var h = new CliHarness();
        var (code, _, error) = await h.RunAsync("service", "install", "--url", "nas:1");
        Assert.Equal(ExitCodes.ConfigInvalid, code);
        Assert.Contains("nas:1", error);
        Assert.Null(h.Machine);
        Assert.DoesNotContain("install", h.Services.Calls);
    }

    [Fact]
    public async Task Install_migrates_legacy_values_once()
    {
        using var h = new CliHarness();
        Directory.CreateDirectory(Path.Combine(h.AppDir, "Watcher"));
        File.WriteAllText(Path.Combine(h.AppDir, "Watcher", "contray.json"), """{ "url": "http://nas:8085" }""");
        h.Services.Status = new ServiceStatus(ServiceState.Running, "\"C:\\old\\Watcher\\ConWatch.exe\" --urls http://0.0.0.0:8085", true);
        await h.RunAsync("service", "install");
        Assert.Equal("http://0.0.0.0:8085", h.Services.Installed!.ListenUrl);
        Assert.Equal("http://nas:8085", h.Services.Installed.ServerUrl);

        // Second run: the machine file now exists; legacy files are ignored even if changed.
        File.WriteAllText(Path.Combine(h.AppDir, "Watcher", "contray.json"), """{ "url": "http://other:1" }""");
        await h.RunAsync("service", "install");
        Assert.Equal("http://nas:8085", h.Services.Installed!.ServerUrl);
    }

    [Fact]
    public async Task Elevated_commands_relaunch_when_not_elevated_and_return_the_child_code()
    {
        using var h = new CliHarness(elevated: false);
        h.Elevation.RelaunchCode = ExitCodes.ElevationDeclined;
        foreach (var args in new[] { new[] { "service", "install", "--port", "1" }, ["service", "uninstall"], ["service", "start"], ["service", "stop"] })
        {
            var (code, _, _) = await h.RunAsync(args);
            Assert.Equal(ExitCodes.ElevationDeclined, code);
            Assert.Equal(args, h.Elevation.RelaunchArgs);
        }
        Assert.Empty(h.Services.Calls);
    }

    [Fact]
    public async Task Status_never_elevates()
    {
        using var h = new CliHarness(elevated: false);
        await h.RunAsync("service", "status");
        Assert.Null(h.Elevation.RelaunchArgs);
    }

    [Fact]
    public async Task Start_stop_uninstall_route_and_report_not_installed()
    {
        using var h = new CliHarness();
        Assert.Equal(ExitCodes.NotInstalled, (await h.RunAsync("service", "start")).Code);
        Assert.Equal(ExitCodes.NotInstalled, (await h.RunAsync("service", "stop")).Code);
        Assert.Equal(ExitCodes.NotInstalled, (await h.RunAsync("service", "uninstall")).Code);
        Assert.DoesNotContain("start", h.Services.Calls);

        h.Services.Status = new ServiceStatus(ServiceState.Stopped, "\"x\" serve", true);
        Assert.Equal(0, (await h.RunAsync("service", "start")).Code);
        Assert.Equal(0, (await h.RunAsync("service", "stop")).Code);
        Assert.Equal(0, (await h.RunAsync("service", "uninstall")).Code);
        Assert.Equal(["start", "stop", "uninstall"], h.Services.Calls.Where(c => c != "status"));
    }

    [Fact]
    public async Task Control_failures_map_to_exit_four()
    {
        using var h = new CliHarness();
        h.Services.Status = new ServiceStatus(ServiceState.Stopped, "\"x\" serve", true);
        h.Services.Throw = new ServiceControlException("sc.exe start failed: 1053");
        var (code, _, error) = await h.RunAsync("service", "start");
        Assert.Equal(ExitCodes.ServiceControlFailed, code);
        Assert.Contains("1053", error);
    }

    [Fact]
    public async Task Status_prints_every_line_and_probes_the_server_url()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:8080", DataDir: @"C:\d", ServerUrl: "http://localhost:8080"));
        h.Services.Status = new ServiceStatus(ServiceState.Running, "\"C:\\app\\conwatch.exe\" serve", true);
        var (code, output, _) = await h.RunAsync("service", "status");
        Assert.Equal(0, code);
        Assert.Contains("Running (auto-start)", output);
        Assert.Contains("\"C:\\app\\conwatch.exe\" serve", output);
        Assert.Contains("http://0.0.0.0:8080", output);
        Assert.Contains(@"C:\d", output);
        Assert.Contains("Healthy", output);
        Assert.Contains("41 ms", output);
        Assert.Equal("http://localhost:8080", h.Health.ProbedUrl);
    }

    [Fact]
    public async Task Status_reports_probe_failures_and_not_installed()
    {
        using var h = new CliHarness();
        h.Services.Status = new ServiceStatus(ServiceState.Stopped, "\"x\" serve", true);
        h.Health.Result = new HealthResult(null, "refused", 3);
        var (_, output, _) = await h.RunAsync("service", "status");
        Assert.Contains("Stopped", output);
        Assert.Contains("refused", output);

        h.Services.Status = ServiceStatus.NotInstalled;
        var (code, output2, _) = await h.RunAsync("service", "status");
        Assert.Equal(ExitCodes.NotInstalled, code);
        Assert.Contains("not installed", output2);
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug`
Expected: build errors (`LegacyInstall`, and `service` unknown → the harness tests fail at runtime with usage errors).

- [x] **Step 3: Implement `LegacyInstall`**

`Programs/ConWatchCli/Services/LegacyInstall.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConWatchCli.Services;

public sealed record LegacyValues(string? ServerUrl, string? ListenUrl);

/// <summary>
/// Reads what the pre-0.10 layout left behind: contray.json beside the old
/// exes, and the --urls baked into the old service's binary path. Used once,
/// when no machine config exists yet.
/// </summary>
public static partial class LegacyInstall
{
    public static LegacyValues Detect(string appDir, string? serviceBinaryPath)
    {
        string? server = null;
        foreach (var sub in new[] { "Watcher", "Tray" })
        {
            var path = Path.Combine(appDir, sub, "contray.json");
            if (!File.Exists(path)) continue;
            try
            {
                var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } value)
                {
                    server = value.TrimEnd('/');
                    break;
                }
            }
            catch (JsonException) { /* unreadable → nothing to migrate */ }
        }

        string? listen = null;
        if (serviceBinaryPath is not null && serviceBinaryPath.Contains("ConWatch.exe", StringComparison.OrdinalIgnoreCase))
        {
            var m = UrlsArgument().Match(serviceBinaryPath);
            if (m.Success) listen = m.Groups[1].Value;
        }
        return new LegacyValues(server, listen);
    }

    [GeneratedRegex("""--urls\s+"?([^\s"]+)"?""")]
    private static partial Regex UrlsArgument();
}
```

- [x] **Step 4: Implement `ServiceCommands` and register it**

`Programs/ConWatchCli/Commands/ServiceCommands.cs`:

```csharp
using System.CommandLine;
using ConConfig;
using ConWatchCli.Services;

namespace ConWatchCli.Commands;

public static class ServiceCommands
{
    public static Command Build(CliServices s, string[] rawArgs)
    {
        var service = new Command("service", "Install, remove, start, stop, or inspect the ConnectionWatcher Windows service.");
        service.Subcommands.Add(Install(s, rawArgs));
        service.Subcommands.Add(Simple(s, rawArgs, "uninstall", "Remove the service, its firewall rule and Start Menu shortcut. Data and config stay.", m => m.UninstallAsync));
        service.Subcommands.Add(Simple(s, rawArgs, "start", "Start the service.", m => m.StartAsync));
        service.Subcommands.Add(Simple(s, rawArgs, "stop", "Stop the service.", m => m.StopAsync));
        service.Subcommands.Add(Status(s));
        return service;
    }

    private static Command Install(CliServices s, string[] rawArgs)
    {
        var port = new Option<int?>("--port") { Description = "Listen on http://0.0.0.0:<port>. Default: first free port from 8080." };
        var url = new Option<string?>("--url") { Description = "Full listen URL (alternative to --port)." };
        var data = new Option<string?>("--data") { Description = "Data folder for conwatch.db." };
        var noStart = new Option<bool>("--no-start") { Description = "Register but do not start." };
        var cmd = new Command("install", "Register the service (elevates), pin the port in the machine config, open the firewall, and start it.");
        cmd.Options.Add(port); cmd.Options.Add(url); cmd.Options.Add(data); cmd.Options.Add(noStart);
        cmd.Validators.Add(r =>
        {
            if (r.GetValue(port) is not null && r.GetValue(url) is not null)
                r.AddError("--port and --url cannot both be given.");
        });
        cmd.SetAction((pr, ct) => CliApp.Elevated(s, rawArgs, async () =>
        {
            var status = await s.Services.StatusAsync(ct);
            var machine = ConfigFile.TryRead(s.Paths.MachineFile, out var warning);
            if (warning is not null) s.Err.WriteLine($"warning: ignoring malformed config {warning}");
            var legacy = machine is null ? LegacyInstall.Detect(s.AppDir, status.BinaryPath) : new LegacyValues(null, null);

            var listen = ResolveListen(s, machine, legacy, pr.GetValue(port), pr.GetValue(url));
            var dataDir = ConfigResolver.DataDir(s.Paths, machine, pr.GetValue(data), s.Env(ConfigResolver.EnvDataDir)).Value;
            if (!ConfigResolver.ValidateUrl(listen, out var urlError)) throw new ConfigException(urlError!);
            if (!ConfigResolver.ValidateDataDir(dataDir, out var dirError)) throw new ConfigException(dirError!);
            var serverUrl = machine?.ServerUrl ?? legacy.ServerUrl ?? ConfigResolver.ListenToServerUrl(listen);

            var next = (machine ?? new ConWatchConfig()) with { ListenUrl = listen, DataDir = dataDir, ServerUrl = serverUrl };
            ConfigFile.WriteAtomic(s.Paths.MachineFile, next);

            await s.Services.InstallAsync(new ServiceSpec(s.CliExePath, listen, dataDir, serverUrl, Start: !pr.GetValue(noStart)), ct);
            s.Out.WriteLine($"ConnectionWatcher service installed{(pr.GetValue(noStart) ? "" : " and started")}.");
            s.Out.WriteLine($"  Dashboard: {serverUrl}");
            s.Out.WriteLine($"  Listen:    {listen}");
            s.Out.WriteLine($"  Data:      {dataDir}");
            s.Out.WriteLine($"  Config:    {s.Paths.MachineFile}");
            return ExitCodes.Ok;
        }, ct));
        return cmd;
    }

    /// <summary>--url, then --port, then env, then machine file, then the old service's --urls, then a probed free port from 8080.</summary>
    private static string ResolveListen(CliServices s, ConWatchConfig? machine, LegacyValues legacy, int? port, string? url)
    {
        if (url is not null) return url;
        if (port is int p)
        {
            if (!s.Ports.IsFree(p)) s.Err.WriteLine($"warning: port {p} is currently in use; installing anyway since you chose it.");
            return $"http://0.0.0.0:{p}";
        }
        var resolved = ConfigResolver.ListenUrl(s.Paths, machine, null, s.Env(ConfigResolver.EnvListenUrl));
        if (resolved.Source != ConfigSource.Default) return resolved.Value;
        if (legacy.ListenUrl is not null) return legacy.ListenUrl;
        var candidate = 8080;
        while (!s.Ports.IsFree(candidate)) candidate++;
        return $"http://0.0.0.0:{candidate}";
    }

    private static Command Simple(CliServices s, string[] rawArgs, string name, string description, Func<IServiceManager, Func<CancellationToken, Task>> pick)
    {
        var cmd = new Command(name, description);
        cmd.SetAction((_, ct) => CliApp.Elevated(s, rawArgs, async () =>
        {
            if ((await s.Services.StatusAsync(ct)).State == ServiceState.NotInstalled)
            {
                s.Err.WriteLine("The ConnectionWatcher service is not installed. Run: conwatch service install");
                return ExitCodes.NotInstalled;
            }
            await pick(s.Services)(ct);
            s.Out.WriteLine($"ConnectionWatcher service: {name} done.");
            return ExitCodes.Ok;
        }, ct));
        return cmd;
    }

    private static Command Status(CliServices s)
    {
        var cmd = new Command("status", "Show whether the service is installed and running, its config, and whether the dashboard answers.");
        cmd.SetAction((_, ct) => CliApp.Guarded(s, async () =>
        {
            var status = await s.Services.StatusAsync(ct);
            if (status.State == ServiceState.NotInstalled)
            {
                s.Out.WriteLine($"Service:  {CliApp.ServiceName} — not installed");
                return ExitCodes.NotInstalled;
            }
            var machine = ConfigFile.TryRead(s.Paths.MachineFile, out _);
            var listen = ConfigResolver.ListenUrl(s.Paths, machine, null, s.Env(ConfigResolver.EnvListenUrl));
            var data = ConfigResolver.DataDir(s.Paths, machine, null, s.Env(ConfigResolver.EnvDataDir));
            var server = ConfigResolver.ServerUrl(s.Paths, null, machine);
            var health = await s.Health.ProbeAsync(server.Value, ct);

            var state = status.State == ServiceState.Other ? status.Detail ?? "unknown" : status.State.ToString();
            s.Out.WriteLine($"Service:  {CliApp.ServiceName} — {state}{(status.AutoStart ? " (auto-start)" : "")}");
            s.Out.WriteLine($"Binary:   {status.BinaryPath}");
            s.Out.WriteLine($"Listen:   {listen.Value}  ({listen.Describe()})");
            s.Out.WriteLine($"Data:     {data.Value}  ({data.Describe()})");
            s.Out.WriteLine(health.Status is not null
                ? $"Health:   {health.Status}  ({server.Value}/api/v1/health, {health.ElapsedMs} ms)"
                : $"Health:   unreachable — {health.Failure}  ({server.Value}/api/v1/health)");
            return ExitCodes.Ok;
        }));
        return cmd;
    }
}
```

In `CliApp.BuildRoot` add `root.Subcommands.Add(ServiceCommands.Build(s, rawArgs));` after `ServeCommand`.

- [x] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug`
Expected: all pass (8 from Task 4 + 4 legacy + 12 service).

- [x] **Step 6: Commit**

```powershell
git add Programs/ConWatchCli Tests/ConWatchCli.Tests
git commit -m "feat(cli): service install/uninstall/start/stop/status with port probe, legacy migration, exit codes"
```

---
### Task 6: Real implementations — health probe, port probe, Windows service manager, elevation

**Files:**
- Create: `Programs/ConWatchCli/Services/HttpHealthProbe.cs`, `Programs/ConWatchCli/Services/TcpPortProbe.cs`, `Programs/ConWatchCli/Windows/WindowsServiceManager.cs`, `Programs/ConWatchCli/Windows/WindowsElevation.cs`
- Modify: `Programs/ConWatchCli/Program.cs` (wire on Windows; delete the `AlwaysElevated`, `UnsupportedHealthProbe`, `UnsupportedPortProbe` placeholders)
- Create: `Tests/ConWatchCli.Tests/HttpHealthProbeTests.cs`, `Tests/ConWatchCli.Tests/TcpPortProbeTests.cs`

**Interfaces:**
- Consumes: Task 4 seams.
- Produces: `HttpHealthProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null)`, `TcpPortProbe`, `WindowsServiceManager(TextWriter log)`, `WindowsElevation(TextWriter output)`.

- [x] **Step 1: Write the failing tests**

`Tests/ConWatchCli.Tests/HttpHealthProbeTests.cs`:

```csharp
using System.Net;
using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class HttpHealthProbeTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested = request.RequestUri;
            return respond(request);
        }
    }

    [Fact]
    public async Task Reads_the_status_word()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "status": "Healthy", "stalled": false }""")
        }));
        var r = await new HttpHealthProbe(handler).ProbeAsync("http://localhost:8080", CancellationToken.None);
        Assert.Equal("Healthy", r.Status);
        Assert.Null(r.Failure);
        Assert.Equal("http://localhost:8080/api/v1/health", handler.Requested!.ToString());
    }

    [Fact]
    public async Task Non_success_reports_the_http_code()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{ "status": "EngineDown" }""")
        }));
        var r = await new HttpHealthProbe(handler).ProbeAsync("http://localhost:8080", CancellationToken.None);
        Assert.Equal("EngineDown", r.Status); // the body still says what is wrong
        Assert.Equal("HTTP 503", r.Failure);
    }

    [Fact]
    public async Task Connection_failure_reports_refused()
    {
        var handler = new Handler(_ => throw new HttpRequestException("No connection could be made"));
        var r = await new HttpHealthProbe(handler).ProbeAsync("http://localhost:1", CancellationToken.None);
        Assert.Null(r.Status);
        Assert.Equal("refused", r.Failure);
    }

    [Fact]
    public async Task Timeout_reports_timed_out()
    {
        var handler = new Handler(async _ => { await Task.Delay(5000); return new HttpResponseMessage(HttpStatusCode.OK); });
        var r = await new HttpHealthProbe(handler, TimeSpan.FromMilliseconds(100)).ProbeAsync("http://localhost:8080", CancellationToken.None);
        Assert.Equal("timed out", r.Failure);
    }
}
```

`Tests/ConWatchCli.Tests/TcpPortProbeTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class TcpPortProbeTests
{
    [Fact]
    public void A_listening_port_is_busy_and_a_released_one_is_free()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new TcpPortProbe();
        Assert.False(probe.IsFree(port));
        listener.Stop();
        Assert.True(probe.IsFree(port));
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug`
Expected: build errors (`HttpHealthProbe`, `TcpPortProbe` missing).

- [x] **Step 3: Implement the probes**

`Programs/ConWatchCli/Services/HttpHealthProbe.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace ConWatchCli.Services;

/// <summary>GET {serverUrl}/api/v1/health, 3 s budget. Read-only; the CLI's only HTTP call.</summary>
public sealed class HttpHealthProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null) : IHealthProbe
{
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = timeout ?? TimeSpan.FromSeconds(3) };

    public async Task<HealthResult> ProbeAsync(string serverUrl, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.GetAsync($"{serverUrl.TrimEnd('/')}/api/v1/health", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            string? status = null;
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("status", out var s)) status = s.GetString();
            }
            catch (JsonException) { /* not our JSON: status stays null */ }
            var failure = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
            return new HealthResult(status, failure, watch.ElapsedMilliseconds);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthResult(null, "timed out", watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException)
        {
            return new HealthResult(null, "refused", watch.ElapsedMilliseconds);
        }
    }
}
```

`Programs/ConWatchCli/Services/TcpPortProbe.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace ConWatchCli.Services;

public sealed class TcpPortProbe : IPortProbe
{
    public bool IsFree(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }
}
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug`
Expected: all pass.

- [x] **Step 5: Implement the Windows service manager (no unit test; exercised by the manual checklist in Task 11)**

`Programs/ConWatchCli/Windows/WindowsServiceManager.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using ConConfig;
using ConWatchCli.Services;
using Microsoft.Win32;

namespace ConWatchCli.Windows;

/// <summary>
/// sc.exe for registration and recovery, ServiceController for state,
/// netsh for the firewall rule, a .url file for the Start Menu shortcut.
/// Mirrors what deploy/windows/install-service.ps1 used to do.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceManager(TextWriter log) : IServiceManager
{
    private const string Name = CliApp.ServiceName;
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ConnectionWatcher Dashboard.url");
    private static string LegacyShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ConnectionWatcher Dashboard.lnk");

    public async Task InstallAsync(ServiceSpec spec, CancellationToken ct)
    {
        if ((await StatusAsync(ct)).State != ServiceState.NotInstalled)
        {
            log.WriteLine("Service exists - reconfiguring.");
            await StopIfRunningAsync(ct);
            Sc("delete", Name);
            await WaitUntilGoneAsync(ct);
        }

        var port = ConfigResolver.PortOf(spec.ListenUrl);
        Sc("create", Name, "binPath=", $"\"{spec.ExePath}\" serve", "start=", "auto", "DisplayName=", Name);
        Sc("description", Name, $"Home internet connectivity monitor (dashboard at {spec.ServerUrl})");
        Sc("failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/30000/restart/60000");

        Run("netsh", ["advfirewall", "firewall", "delete", "rule", $"name={Name}"], allowFailure: true);
        Run("netsh", ["advfirewall", "firewall", "add", "rule", $"name={Name}", "dir=in", "action=allow",
            "protocol=TCP", $"localport={port}", "profile=private,domain"]);

        if (File.Exists(LegacyShortcutPath)) File.Delete(LegacyShortcutPath);
        File.WriteAllText(ShortcutPath, $"[InternetShortcut]\r\nURL={spec.ServerUrl}\r\n");

        if (spec.Start)
            await StartAsync(ct);
    }

    public async Task UninstallAsync(CancellationToken ct)
    {
        await StopIfRunningAsync(ct);
        Sc("delete", Name);
        Run("netsh", ["advfirewall", "firewall", "delete", "rule", $"name={Name}"], allowFailure: true);
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        if (File.Exists(LegacyShortcutPath)) File.Delete(LegacyShortcutPath);
        await WaitUntilGoneAsync(ct);
    }

    public Task StartAsync(CancellationToken ct)
    {
        using var sc = new ServiceController(Name);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            try { sc.WaitForStatus(ServiceControllerStatus.Running, StateTimeout); }
            catch (System.ServiceProcess.TimeoutException) { throw new ServiceControlException($"{Name} did not reach Running within {StateTimeout.TotalSeconds:0} s; check the Windows event log."); }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => StopIfRunningAsync(ct);

    public Task<ServiceStatus> StatusAsync(CancellationToken ct)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Name}");
        if (key is null)
            return Task.FromResult(ServiceStatus.NotInstalled);
        var binary = key.GetValue("ImagePath") as string;
        var autoStart = key.GetValue("Start") is int start && start == 2;
        using var sc = new ServiceController(Name);
        var state = sc.Status switch
        {
            ServiceControllerStatus.Running => ServiceState.Running,
            ServiceControllerStatus.Stopped => ServiceState.Stopped,
            _ => ServiceState.Other,
        };
        return Task.FromResult(new ServiceStatus(state, binary, autoStart, state == ServiceState.Other ? sc.Status.ToString() : null));
    }

    private Task StopIfRunningAsync(CancellationToken ct)
    {
        using var sc = new ServiceController(Name);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending or ServiceControllerStatus.Paused)
        {
            sc.Stop();
            try { sc.WaitForStatus(ServiceControllerStatus.Stopped, StateTimeout); }
            catch (System.ServiceProcess.TimeoutException) { throw new ServiceControlException($"{Name} did not stop within {StateTimeout.TotalSeconds:0} s."); }
        }
        return Task.CompletedTask;
    }

    private async Task WaitUntilGoneAsync(CancellationToken ct)
    {
        // sc delete is asynchronous: the entry lingers until every handle closes.
        for (var i = 0; i < 50; i++)
        {
            if ((await StatusAsync(ct)).State == ServiceState.NotInstalled) return;
            await Task.Delay(200, ct);
        }
        throw new ServiceControlException($"{Name} is marked for deletion but still present; close services.msc and retry.");
    }

    private void Sc(params string[] args) => Run("sc.exe", args);

    private void Run(string exe, string[] args, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new ServiceControlException($"could not start {exe}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !allowFailure)
            throw new ServiceControlException($"{exe} {string.Join(' ', args)} failed (exit {p.ExitCode}): {(stderr + stdout).Trim()}");
    }
}
```

- [x] **Step 6: Implement elevation**

`Programs/ConWatchCli/Windows/WindowsElevation.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using ConWatchCli.Services;

namespace ConWatchCli.Windows;

/// <summary>
/// One UAC prompt: relaunch this exe with the same arguments plus
/// --__output <temp file>, wait, echo the file, return the child's exit code.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsElevation(TextWriter output) : IElevation
{
    private const int ErrorCancelled = 1223;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot determine the running executable");
        var capture = Path.Combine(Path.GetTempPath(), $"conwatch-{Guid.NewGuid():N}.log");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--__output");
        psi.ArgumentList.Add(capture);

        Process child;
        try
        {
            child = Process.Start(psi) ?? throw new InvalidOperationException("elevated launch returned no process");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            output.WriteLine("This command needs administrator rights.");
            return ExitCodes.ElevationDeclined;
        }

        using (child)
        {
            await child.WaitForExitAsync(ct);
            if (File.Exists(capture))
            {
                output.Write(await File.ReadAllTextAsync(capture, ct));
                File.Delete(capture);
            }
            return child.ExitCode;
        }
    }
}
```

- [x] **Step 7: Wire them in `Program.cs`**

Replace the placeholder lines and delete the `AlwaysElevated`, `UnsupportedHealthProbe`, `UnsupportedPortProbe` file-local classes:

```csharp
    IServiceManager serviceManager = OperatingSystem.IsWindows() ? new ConWatchCli.Windows.WindowsServiceManager(output) : new UnsupportedServiceManager();
    IElevation elevation = OperatingSystem.IsWindows() ? new ConWatchCli.Windows.WindowsElevation(output) : new NotElevatable();
    // ...
        Services: serviceManager,
        Elevation: elevation,
        Health: new HttpHealthProbe(),
        Ports: new TcpPortProbe(),
```

and add, in place of `AlwaysElevated`:

```csharp
/// <summary>Non-Windows: there is no UAC; run inline and let the OS refuse.</summary>
file sealed class NotElevatable : IElevation
{
    public bool IsElevated => true;
    public Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct) => Task.FromResult(ExitCodes.Ok);
}
```

`Microsoft.Win32.Registry` ships in the shared framework for `net10.0`; if the build cannot find it, add `<PackageReference Include="Microsoft.Win32.Registry" Version="5.0.0" />` to `ConWatchCli.csproj`.

- [x] **Step 8: Build with warnings as errors for the platform analyzer**

Run: `dotnet build Programs/ConWatchCli -c Debug -warnaserror:CA1416`
Expected: succeeds. Any CA1416 means a Windows-only call escaped the `[SupportedOSPlatform("windows")]` classes or the `OperatingSystem.IsWindows()` guard.

- [x] **Step 9: Run all CLI tests, then commit**

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug`
Expected: all pass.

```powershell
git add Programs/ConWatchCli Tests/ConWatchCli.Tests
git commit -m "feat(cli): HTTP health probe, TCP port probe, Windows service manager (sc/netsh/.url shortcut), UAC self-elevation with output capture"
```

---

### Task 7: `config show` and `config set`

**Files:**
- Create: `Programs/ConWatchCli/Commands/ConfigCommands.cs`
- Modify: `Programs/ConWatchCli/CliApp.cs` (register)
- Create: `Tests/ConWatchCli.Tests/ConfigCommandTests.cs`

**Interfaces:**
- Consumes: Task 4 harness, `ConfigFile`, `ConfigResolver`.
- Produces: `ConfigCommands.Build(CliServices s, string[] rawArgs)`.

- [x] **Step 1: Write the failing tests**

`Tests/ConWatchCli.Tests/ConfigCommandTests.cs`:

```csharp
using ConConfig;

namespace ConWatchCli.Tests;

public sealed class ConfigCommandTests
{
    [Fact]
    public async Task Show_prints_each_value_with_its_source()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:9000", ServerUrl: "http://nas:9000"));
        h.WriteUser(new ConWatchConfig(ServerUrl: "http://user:1", Notifications: false));
        h.Env[ConfigResolver.EnvDataDir] = @"E:\env";
        var (code, output, _) = await h.RunAsync("config", "show");
        Assert.Equal(0, code);
        Assert.Contains($"listen-url     http://0.0.0.0:9000  (machine: {h.Paths.MachineFile})", output);
        Assert.Contains(@"data-dir       E:\env  (environment)", output);
        Assert.Contains($"server-url     http://user:1  (user: {h.Paths.UserFile})", output);
        Assert.Contains($"notifications  false  (user: {h.Paths.UserFile})", output);
    }

    [Fact]
    public async Task Show_with_nothing_configured_says_default_everywhere()
    {
        using var h = new CliHarness();
        var (_, output, _) = await h.RunAsync("config", "show");
        Assert.Equal(4, output.Split("(default)").Length - 1);
    }

    [Fact]
    public async Task Show_warns_about_a_malformed_file_and_continues()
    {
        using var h = new CliHarness();
        Directory.CreateDirectory(Path.GetDirectoryName(h.Paths.MachineFile)!);
        File.WriteAllText(h.Paths.MachineFile, "{ nope");
        var (code, output, error) = await h.RunAsync("config", "show");
        Assert.Equal(0, code);
        Assert.Contains(h.Paths.MachineFile, error);
        Assert.Contains("(default)", output);
    }

    [Fact]
    public async Task Set_listen_url_writes_the_machine_file_and_keeps_other_values()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(DataDir: @"D:\keep", ServerUrl: "http://keep:1"));
        var (code, output, _) = await h.RunAsync("config", "set", "listen-url", "http://0.0.0.0:7070/");
        Assert.Equal(0, code);
        Assert.Equal("http://0.0.0.0:7070", h.Machine!.ListenUrl);
        Assert.Equal(@"D:\keep", h.Machine.DataDir);
        Assert.Equal("http://keep:1", h.Machine.ServerUrl);
        Assert.Contains(h.Paths.MachineFile, output);
        Assert.Contains("restart", output);
    }

    [Fact]
    public async Task Set_data_dir_creates_it()
    {
        using var h = new CliHarness();
        var dir = Path.Combine(h.Root, "newdata");
        Assert.Equal(0, (await h.RunAsync("config", "set", "data-dir", dir)).Code);
        Assert.Equal(dir, h.Machine!.DataDir);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task Set_server_url_goes_to_machine_or_user_file()
    {
        using var h = new CliHarness();
        Assert.Equal(0, (await h.RunAsync("config", "set", "server-url", "http://nas:8080")).Code);
        Assert.Equal("http://nas:8080", h.Machine!.ServerUrl);
        Assert.Null(h.User);

        Assert.Equal(0, (await h.RunAsync("config", "set", "server-url", "http://me:8080", "--user")).Code);
        Assert.Equal("http://me:8080", h.User!.ServerUrl);
        Assert.Equal("http://nas:8080", h.Machine!.ServerUrl);
    }

    [Fact]
    public async Task Set_rejects_bad_values_without_writing()
    {
        using var h = new CliHarness();
        Assert.Equal(ExitCodes.ConfigInvalid, (await h.RunAsync("config", "set", "listen-url", "nope")).Code);
        Assert.Equal(ExitCodes.ConfigInvalid, (await h.RunAsync("config", "set", "data-dir", "relative")).Code);
        Assert.Null(h.Machine);
        Assert.Equal(ExitCodes.Usage, (await h.RunAsync("config", "set", "colour", "red")).Code);
        Assert.Equal(ExitCodes.Usage, (await h.RunAsync("config", "set", "listen-url", "http://x", "--user")).Code);
    }

    [Fact]
    public async Task Set_elevates_unless_user_scoped()
    {
        using var h = new CliHarness(elevated: false);
        h.Elevation.RelaunchCode = 7;
        Assert.Equal(7, (await h.RunAsync("config", "set", "server-url", "http://nas:8080")).Code);
        Assert.Equal(["config", "set", "server-url", "http://nas:8080"], h.Elevation.RelaunchArgs);
        Assert.Null(h.Machine);

        Assert.Equal(0, (await h.RunAsync("config", "set", "server-url", "http://me:8080", "--user")).Code);
        Assert.Equal("http://me:8080", h.User!.ServerUrl);

        Assert.Equal(0, (await h.RunAsync("config", "show")).Code);
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug`
Expected: the eight new tests fail with usage errors (`config` unknown).

- [x] **Step 3: Implement and register**

`Programs/ConWatchCli/Commands/ConfigCommands.cs`:

```csharp
using System.CommandLine;
using ConConfig;
using ConWatchCli.Services;

namespace ConWatchCli.Commands;

public static class ConfigCommands
{
    private static readonly string[] Keys = ["listen-url", "data-dir", "server-url"];

    public static Command Build(CliServices s, string[] rawArgs)
    {
        var config = new Command("config", "Show or change the config files (listen URL, data folder, tray server URL).");
        config.Subcommands.Add(Show(s));
        config.Subcommands.Add(Set(s, rawArgs));
        return config;
    }

    private static Command Show(CliServices s)
    {
        var cmd = new Command("show", "Print every effective value and where it comes from.");
        cmd.SetAction((_, _) => CliApp.Guarded(s, () =>
        {
            var machine = ConfigFile.TryRead(s.Paths.MachineFile, out var mWarn);
            var user = ConfigFile.TryRead(s.Paths.UserFile, out var uWarn);
            foreach (var w in new[] { mWarn, uWarn }.Where(w => w is not null))
                s.Err.WriteLine($"warning: ignoring malformed config {w}");

            var listen = ConfigResolver.ListenUrl(s.Paths, machine, null, s.Env(ConfigResolver.EnvListenUrl));
            var data = ConfigResolver.DataDir(s.Paths, machine, null, s.Env(ConfigResolver.EnvDataDir));
            var server = ConfigResolver.ServerUrl(s.Paths, user, machine);
            var notifications = user?.Notifications is bool n
                ? new Resolved<string>(n ? "true" : "false", ConfigSource.User, s.Paths.UserFile)
                : new Resolved<string>("true", ConfigSource.Default);

            s.Out.WriteLine($"listen-url     {listen.Value}  ({listen.Describe()})");
            s.Out.WriteLine($"data-dir       {data.Value}  ({data.Describe()})");
            s.Out.WriteLine($"server-url     {server.Value}  ({server.Describe()})");
            s.Out.WriteLine($"notifications  {notifications.Value}  ({notifications.Describe()})");
            return Task.FromResult(ExitCodes.Ok);
        }));
        return cmd;
    }

    private static Command Set(CliServices s, string[] rawArgs)
    {
        var key = new Argument<string>("key") { Description = "listen-url | data-dir | server-url" };
        var value = new Argument<string>("value");
        var user = new Option<bool>("--user") { Description = "Write the per-user tray file instead of the machine file (server-url only; no elevation)." };
        var cmd = new Command("set", "Change one value. Machine values elevate; the service picks them up on the next restart.");
        cmd.Arguments.Add(key); cmd.Arguments.Add(value); cmd.Options.Add(user);
        cmd.Validators.Add(r =>
        {
            var k = r.GetValue(key);
            if (k is not null && !Keys.Contains(k)) r.AddError($"Unknown key '{k}'. Expected one of: {string.Join(", ", Keys)}.");
            if (r.GetValue(user) && k != "server-url") r.AddError("--user applies to server-url only.");
        });
        cmd.SetAction((pr, ct) =>
        {
            var k = pr.GetValue(key)!;
            var v = pr.GetValue(value)!;
            var perUser = pr.GetValue(user);
            Func<Task<int>> body = () => Task.FromResult(Apply(s, k, v, perUser));
            return perUser ? CliApp.Guarded(s, body) : CliApp.Elevated(s, rawArgs, body, ct);
        });
        return cmd;
    }

    private static int Apply(CliServices s, string key, string value, bool perUser)
    {
        var path = perUser ? s.Paths.UserFile : s.Paths.MachineFile;
        var current = ConfigFile.TryRead(path, out var warn) ?? new ConWatchConfig();
        if (warn is not null) s.Err.WriteLine($"warning: replacing malformed config {warn}");

        ConWatchConfig next;
        switch (key)
        {
            case "listen-url":
            case "server-url":
                if (!ConfigResolver.ValidateUrl(value, out var urlError)) throw new ConfigException(urlError!);
                var url = value.TrimEnd('/');
                next = key == "listen-url" ? current with { ListenUrl = url } : current with { ServerUrl = url };
                break;
            default: // data-dir
                if (!ConfigResolver.ValidateDataDir(value, out var dirError)) throw new ConfigException(dirError!);
                next = current with { DataDir = value };
                break;
        }
        ConfigFile.WriteAtomic(path, next);
        s.Out.WriteLine($"{key} = {value}  written to {path}");
        if (!perUser && key != "server-url")
            s.Out.WriteLine("The service reads this on its next start: conwatch service stop, then conwatch service start.");
        return ExitCodes.Ok;
    }
}
```

In `CliApp.BuildRoot` add `root.Subcommands.Add(ConfigCommands.Build(s, rawArgs));`.

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug`
Expected: all pass.

- [x] **Step 5: Commit**

```powershell
git add Programs/ConWatchCli Tests/ConWatchCli.Tests
git commit -m "feat(cli): config show / config set with provenance, validation, per-user scope"
```

---

### Task 8: Tray moves to `Programs/ConWatchTray`; `tray`, `tray install | uninstall`

**Files:**
- Move: `Programs/ConTray/*` → `Programs/ConWatchTray/*` (git mv), delete `Programs/ConTray`
- Create: `Programs/ConWatchTray/ConWatchTray.csproj` (renamed), rewrite `Programs/ConWatchTray/TraySettings.cs`
- Create: `Libraries/ConConfig/LegacyTrayFile.cs`, `Tests/ConConfig.Tests/LegacyTrayFileTests.cs`
- Create: `Programs/ConWatchCli/Commands/TrayCommands.cs`, `Programs/ConWatchCli/Services/ProcessTrayLauncher.cs`, `Programs/ConWatchCli/Windows/WindowsTrayAutostart.cs`
- Modify: `Programs/ConWatchCli/CliApp.cs`, `Programs/ConWatchCli/Program.cs`, `ConnectionWatcher.sln`
- Create: `Tests/ConWatchCli.Tests/TrayCommandTests.cs`

**Interfaces:**
- Consumes: `ConfigPaths.LegacyUserFile`, `ConfigResolver`, `ConfigFile`, seams `ITrayLauncher`/`ITrayAutostart`.
- Produces: `LegacyTrayFile.TryRead(string path) : ConWatchConfig?`, `TrayCommands.Build(CliServices s)`.

- [x] **Step 1: Failing test for the legacy per-user file**

`Tests/ConConfig.Tests/LegacyTrayFileTests.cs`:

```csharp
using ConConfig;

namespace ConConfig.Tests;

public sealed class LegacyTrayFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("legacytray").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Maps_url_and_notifications()
    {
        var path = Path.Combine(_root, "contray-settings.json");
        File.WriteAllText(path, """{ "url": "http://nas:8080/", "notifications": false }""");
        var c = LegacyTrayFile.TryRead(path)!;
        Assert.Equal("http://nas:8080", c.ServerUrl);
        Assert.False(c.Notifications);
    }

    [Fact]
    public void Missing_or_malformed_is_null()
    {
        Assert.Null(LegacyTrayFile.TryRead(Path.Combine(_root, "none.json")));
        var bad = Path.Combine(_root, "bad.json");
        File.WriteAllText(bad, "nope");
        Assert.Null(LegacyTrayFile.TryRead(bad));
    }
}
```

Run: `dotnet test Tests/ConConfig.Tests -c Debug` → build error. Then add `Libraries/ConConfig/LegacyTrayFile.cs`:

```csharp
using System.Text.Json;

namespace ConConfig;

/// <summary>The pre-0.10 tray file: { "url": ..., "notifications": ... }.</summary>
public static class LegacyTrayFile
{
    public static ConWatchConfig? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            string? url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()?.TrimEnd('/') : null;
            bool? notifications = root.TryGetProperty("notifications", out var n) && n.ValueKind is JsonValueKind.True or JsonValueKind.False ? n.GetBoolean() : null;
            return new ConWatchConfig(ServerUrl: url is { Length: > 0 } ? url : null, Notifications: notifications);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
```

Run: `dotnet test Tests/ConConfig.Tests -c Debug` → all pass.

- [x] **Step 2: Move the tray project**

```powershell
git mv Programs/ConTray Programs/ConWatchTray
git mv Programs/ConWatchTray/ConTray.csproj Programs/ConWatchTray/ConWatchTray.csproj
dotnet sln remove Programs/ConWatchTray/ConTray.csproj
dotnet sln add Programs/ConWatchTray
Get-ChildItem Programs/ConWatchTray/*.cs | ForEach-Object { (Get-Content $_ -Raw) -replace 'namespace ConTray;', 'namespace ConWatchTray;' | Set-Content $_ -NoNewline }
Remove-Item -Recurse -Force Programs/ConWatchTray/bin, Programs/ConWatchTray/obj -ErrorAction SilentlyContinue
```

`Programs/ConWatchTray/ConWatchTray.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <AssemblyName>conwatch-tray</AssemblyName>
    <RootNamespace>ConWatchTray</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyTitle>ConnectionWatcher Tray</AssemblyTitle>
    <!-- Version, Company and Product come from Directory.Build.props. -->
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Libraries\ConConfig\ConConfig.csproj" />
  </ItemGroup>

</Project>
```

- [x] **Step 3: Rewrite `TraySettings.cs` over ConConfig (same public surface `Program.cs` and `SettingsForm.cs` use)**

```csharp
using ConConfig;

namespace ConWatchTray;

/// <summary>
/// Server URL and notifications for the tray: per-user tray.json (override)
/// over the machine conwatch.json (installed default) over localhost. The
/// old contray-settings.json is read when tray.json does not exist yet and
/// is rewritten as tray.json on the first save.
/// </summary>
public sealed class TraySettings
{
    public const string FallbackUrl = ConfigResolver.FallbackServerUrl;

    private readonly ConfigPaths _paths;
    private readonly ConWatchConfig? _machine;
    private ConWatchConfig _user;

    public TraySettings(ConfigPaths? paths = null)
    {
        _paths = paths ?? ConfigPaths.Default();
        _machine = ConfigFile.TryRead(_paths.MachineFile, out _);
        _user = ConfigFile.TryRead(_paths.UserFile, out _)
            ?? LegacyTrayFile.TryRead(_paths.LegacyUserFile)
            ?? new ConWatchConfig();
    }

    public string? OverrideUrl => _user.ServerUrl;
    public bool NotificationsEnabled => ConfigResolver.Notifications(_user);
    public bool HasOverride => OverrideUrl is { Length: > 0 };

    /// <summary>The machine config's server URL (or its listen URL rewritten), if an install wrote one.</summary>
    public string? InstalledDefaultUrl =>
        ConfigResolver.ServerUrl(_paths, null, _machine) is { Source: ConfigSource.Machine } r ? r.Value : null;

    /// <summary>override > installed default > localhost fallback.</summary>
    public string EffectiveUrl => ConfigResolver.ServerUrl(_paths, _user, _machine).Value;

    public string EffectiveSource =>
        HasOverride ? "custom"
        : InstalledDefaultUrl is not null ? "installed default"
        : "built-in fallback";

    public void SetOverride(string url) => Save(_user with { ServerUrl = url.TrimEnd('/') });
    public void ClearOverride() => Save(_user with { ServerUrl = null });
    public void SetNotifications(bool enabled) => Save(_user with { Notifications = enabled });

    private void Save(ConWatchConfig next)
    {
        _user = next;
        try { ConfigFile.WriteAtomic(_paths.UserFile, next); }
        catch (Exception) { /* settings that fail to persist still apply for this session */ }
    }
}
```

Build: `dotnet build Programs/ConWatchTray -c Debug` → 0 warnings. `SettingsForm.cs` compiles unchanged because every member it uses still exists.

- [x] **Step 4: Failing tests for the tray commands**

`Tests/ConWatchCli.Tests/TrayCommandTests.cs`:

```csharp
namespace ConWatchCli.Tests;

public sealed class TrayCommandTests
{
    [Fact]
    public async Task Tray_launches_the_exe_beside_the_cli()
    {
        using var h = new CliHarness(elevated: false);
        var (code, _, _) = await h.RunAsync("tray");
        Assert.Equal(0, code);
        Assert.Equal(h.TrayExePath(), h.TrayLauncher.Launched);
        Assert.Null(h.Elevation.RelaunchArgs);
    }

    [Fact]
    public async Task Tray_missing_exe_is_exit_three()
    {
        using var h = new CliHarness { TrayLauncher = new FakeTrayLauncher(present: false) };
        var (code, _, error) = await h.RunAsync("tray");
        Assert.Equal(ExitCodes.NotInstalled, code);
        Assert.Contains("conwatch-tray", error);
    }

    [Fact]
    public async Task Tray_install_registers_autostart_and_optionally_writes_the_user_url()
    {
        using var h = new CliHarness(elevated: false);
        File.WriteAllText(h.TrayExePath(), "");
        Assert.Equal(0, (await h.RunAsync("tray", "install")).Code);
        Assert.Equal(h.TrayExePath(), h.TrayAutostart.InstalledPath);
        Assert.Null(h.User);

        Assert.Equal(0, (await h.RunAsync("tray", "install", "--url", "http://nas:8080/")).Code);
        Assert.Equal("http://nas:8080", h.User!.ServerUrl);
        Assert.Null(h.Elevation.RelaunchArgs);
    }

    [Fact]
    public async Task Tray_install_rejects_a_bad_url_and_a_missing_exe()
    {
        using var h = new CliHarness();
        Assert.Equal(ExitCodes.NotInstalled, (await h.RunAsync("tray", "install")).Code);
        File.WriteAllText(h.TrayExePath(), "");
        Assert.Equal(ExitCodes.ConfigInvalid, (await h.RunAsync("tray", "install", "--url", "nas")).Code);
        Assert.False(h.TrayAutostart.IsInstalled);
    }

    [Fact]
    public async Task Tray_uninstall_removes_autostart()
    {
        using var h = new CliHarness();
        h.TrayAutostart.Install("x");
        Assert.Equal(0, (await h.RunAsync("tray", "uninstall")).Code);
        Assert.False(h.TrayAutostart.IsInstalled);
    }
}
```

Add to `CliHarness`: `public string TrayExePath() => Path.Combine(AppDir, OperatingSystem.IsWindows() ? "conwatch-tray.exe" : "conwatch-tray");`

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug` → the five fail with usage errors.

- [x] **Step 5: Implement the tray commands and real launcher/autostart**

`Programs/ConWatchCli/Commands/TrayCommands.cs`:

```csharp
using System.CommandLine;
using ConConfig;
using ConWatchCli.Services;

namespace ConWatchCli.Commands;

public static class TrayCommands
{
    public static Command Build(CliServices s)
    {
        var tray = new Command("tray", "Start the tray companion now, or register/unregister it to start when you sign in.");
        tray.SetAction((_, _) => CliApp.Guarded(s, () =>
        {
            if (!s.TrayLauncher.Launch(s.TrayExePath))
            {
                s.Err.WriteLine($"{Path.GetFileName(s.TrayExePath)} not found beside conwatch.");
                return Task.FromResult(ExitCodes.NotInstalled);
            }
            return Task.FromResult(ExitCodes.Ok);
        }));

        var url = new Option<string?>("--url") { Description = "Also set this user's server URL." };
        var install = new Command("install", "Start the tray when this user signs in (no elevation).");
        install.Options.Add(url);
        install.SetAction((pr, _) => CliApp.Guarded(s, () =>
        {
            if (!File.Exists(s.TrayExePath))
            {
                s.Err.WriteLine($"{Path.GetFileName(s.TrayExePath)} not found beside conwatch.");
                return Task.FromResult(ExitCodes.NotInstalled);
            }
            if (pr.GetValue(url) is { } u)
            {
                if (!ConfigResolver.ValidateUrl(u, out var error)) throw new ConfigException(error!);
                var current = ConfigFile.TryRead(s.Paths.UserFile, out _) ?? new ConWatchConfig();
                ConfigFile.WriteAtomic(s.Paths.UserFile, current with { ServerUrl = u.TrimEnd('/') });
                s.Out.WriteLine($"server-url = {u.TrimEnd('/')}  written to {s.Paths.UserFile}");
            }
            s.TrayAutostart.Install(s.TrayExePath);
            s.Out.WriteLine("Tray companion will start when you sign in.");
            return Task.FromResult(ExitCodes.Ok);
        }));

        var uninstall = new Command("uninstall", "Stop starting the tray at sign-in.");
        uninstall.SetAction((_, _) => CliApp.Guarded(s, () =>
        {
            s.TrayAutostart.Uninstall();
            s.Out.WriteLine("Tray companion autostart removed.");
            return Task.FromResult(ExitCodes.Ok);
        }));

        tray.Subcommands.Add(install);
        tray.Subcommands.Add(uninstall);
        return tray;
    }
}
```

`Programs/ConWatchCli/Services/ProcessTrayLauncher.cs`:

```csharp
using System.Diagnostics;

namespace ConWatchCli.Services;

public sealed class ProcessTrayLauncher : ITrayLauncher
{
    public bool Launch(string trayExePath)
    {
        if (!File.Exists(trayExePath)) return false;
        Process.Start(new ProcessStartInfo(trayExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(trayExePath) });
        return true;
    }
}
```

`Programs/ConWatchCli/Windows/WindowsTrayAutostart.cs`:

```csharp
using System.Runtime.Versioning;
using ConWatchCli.Services;
using Microsoft.Win32;

namespace ConWatchCli.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsTrayAutostart : ITrayAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ConnectionWatcherTray";

    public bool IsInstalled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) is not null; }
    }

    public void Install(string trayExePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{trayExePath}\"");
    }

    public void Uninstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
```

In `Program.cs`: `TrayLauncher: new ProcessTrayLauncher()`, and `ITrayAutostart trayAutostart = OperatingSystem.IsWindows() ? new ConWatchCli.Windows.WindowsTrayAutostart() : new UnsupportedTrayAutostart();` (keep the `UnsupportedTrayAutostart` file-local class; delete `UnsupportedTrayLauncher`). In `CliApp.BuildRoot` add `root.Subcommands.Add(TrayCommands.Build(s));`.

- [x] **Step 6: Verify**

Extend `RootCommandTests.No_arguments_prints_help_and_returns_zero` with `Assert.Contains("service", output); Assert.Contains("config", output); Assert.Contains("tray", output);` and drop its "Task 8 extends" comment.

Run: `dotnet test Tests/ConWatchCli.Tests -c Debug` and `dotnet build ConnectionWatcher.sln -c Debug`
Expected: all pass; solution builds with 0 warnings; `Programs/ConTray` no longer exists; `grep -rn "ConTray" --include=*.cs --include=*.csproj --include=*.sln .` returns nothing outside `docs/` and `staging/`.

- [x] **Step 7: Commit**

```powershell
git add -A Programs/ConTray Programs/ConWatchTray Programs/ConWatchCli Libraries/ConConfig Tests ConnectionWatcher.sln
git commit -m "feat(tray): move ConTray to ConWatchTray (conwatch-tray.exe) over ConConfig; conwatch tray / tray install / tray uninstall"
```

---
### Task 9: Build script, Inno wizard, zip, workflows; delete the PowerShell glue

**Files:**
- Modify: `Build-Installer.ps1` (whole file), `deploy/windows/installer.iss` (whole file), `.github/workflows/release.yml`, `.github/workflows/ci.yml`
- Create: `deploy/windows/README.txt`
- Delete: `Build-Tray.ps1`, `Build-Watcher.ps1`, `Publish-Tray.ps1`, `Publish-Watcher.ps1`, `Install-Tray.ps1`, `Install-Watcher.ps1`, `Uninstall-Tray.ps1`, `Uninstall-Watcher.ps1`, `deploy/windows/common.ps1`, `deploy/windows/install-service.ps1`, `deploy/windows/uninstall-service.ps1`, `deploy/windows/install-tray.ps1`, `deploy/windows/uninstall-tray.ps1`, `deploy/windows/configure-tray.ps1`

**Interfaces:**
- Consumes: the two exes and their exit codes (Tasks 4–8).
- Produces: `staging/app/`, `dist/ConnectionWatcher-Setup-<v>.exe`, `dist/ConnectionWatcher-<v>-win-x64.zip`.

- [x] **Step 1: Delete the scripts**

```powershell
git rm Build-Tray.ps1 Build-Watcher.ps1 Publish-Tray.ps1 Publish-Watcher.ps1 Install-Tray.ps1 Install-Watcher.ps1 Uninstall-Tray.ps1 Uninstall-Watcher.ps1
git rm deploy/windows/common.ps1 deploy/windows/install-service.ps1 deploy/windows/uninstall-service.ps1 deploy/windows/install-tray.ps1 deploy/windows/uninstall-tray.ps1 deploy/windows/configure-tray.ps1
```

- [x] **Step 2: Write `deploy/windows/README.txt`**

```text
ConnectionWatcher - manual install (Windows, x64)

1. Unzip anywhere, for example C:\Program Files\DSoftCo\ConnectionWatcher.
2. From a terminal in that folder:

     conwatch service install --port 8080
     conwatch tray install
     conwatch service status

   The first command asks for administrator rights once. It registers the
   ConnectionWatcher service, opens the firewall for the port, adds a Start
   Menu shortcut to the dashboard, and starts the service.

Everything else:   conwatch --help

Config:  %ProgramData%\DSoftCo\ConnectionWatcher\conwatch.json
Data:    %ProgramData%\DSoftCo\ConnectionWatcher\data
Tray:    %AppData%\DSoftCo\ConnectionWatcher\tray.json   (per user)

Change the port later:
     conwatch config set listen-url http://0.0.0.0:9090
     conwatch service stop
     conwatch service start

Remove:  conwatch service uninstall
         conwatch tray uninstall
         then delete the folder. Data and config are left in place.
```

- [x] **Step 3: Rewrite `Build-Installer.ps1`**

```powershell
# Builds ConnectionWatcher-Setup-<version>.exe and
# ConnectionWatcher-<version>-win-x64.zip in dist\.
# Publishes conwatch.exe (the command + web host) and conwatch-tray.exe
# self-contained win-x64 into ONE staging folder (they share the runtime),
# then compiles the Inno Setup script and zips the same folder. Requires
# Inno Setup 6 (winget install JRSoftware.InnoSetup).
#
# -Version stamps the installer name, the zip name and the binaries inside.
# The release workflow passes the tag's version; by hand it defaults to
# Directory.Build.props' "<prefix>-<suffix>".
[CmdletBinding()]
param([string]$Version = "")
$ErrorActionPreference = "Stop"

if (-not $Version) {
    $props = Get-Content (Join-Path $PSScriptRoot "Directory.Build.props") -Raw
    $prefix = [regex]::Match($props, "<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value
    $suffix = [regex]::Match($props, "<VersionSuffix>([^<]+)</VersionSuffix>").Groups[1].Value
    if (-not $prefix) { throw "VersionPrefix not found in Directory.Build.props" }
    $Version = if ($suffix) { "$prefix-$suffix" } else { $prefix }
}

$staging = Join-Path $PSScriptRoot "staging"
$app = Join-Path $staging "app"
$dist = Join-Path $PSScriptRoot "dist"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $app, $dist | Out-Null

foreach ($project in "Programs\ConWatchCli", "Programs\ConWatchTray") {
    Write-Host "Publishing $project (self-contained win-x64) into staging\app..."
    dotnet publish (Join-Path $PSScriptRoot $project) -c Release `
        -r win-x64 --self-contained true -o $app "/p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
}
# The host's own apphost rides along with the project reference; the command is conwatch.exe.
Remove-Item (Join-Path $app "ConWatch.exe") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot "deploy\windows\README.txt") $app
Copy-Item (Join-Path $PSScriptRoot "LICENSE") $app

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" "/DStagingDir=$app" "/O$dist" `
    (Join-Path $PSScriptRoot "deploy\windows\installer.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$zip = Join-Path $dist "ConnectionWatcher-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip

Write-Host ""
Write-Host "Installer ready: $(Join-Path $dist "ConnectionWatcher-Setup-$Version.exe")" -ForegroundColor Green
Write-Host "Zip ready:       $zip" -ForegroundColor Green
```

- [x] **Step 4: Rewrite `deploy/windows/installer.iss`**

```ini
; ConnectionWatcher Windows installer (Inno Setup 6).
; Built by Build-Installer.ps1, which publishes conwatch.exe and
; conwatch-tray.exe into one staging folder and passes the defines below.
; Do not compile directly without them.
;
; The wizard is a thin front end over the conwatch command: every install
; and uninstall step is a `conwatch ...` call, so the zip install and this
; one behave the same. Uninstall never deletes ProgramData (data + config)
; without asking.

#ifndef AppVersion
  #define AppVersion "0.9.0"
#endif
#ifndef StagingDir
  #error Pass /DStagingDir=<path> (Build-Installer.ps1 does this)
#endif

[Setup]
AppId={{7C1B3E82-4A9D-4F1E-9C3A-5D8E2F6B7A10}
AppName=ConnectionWatcher
AppVersion={#AppVersion}
AppPublisher=DSoftCo
DefaultDirName={commonpf}\DSoftCo\ConnectionWatcher
DisableProgramGroupPage=yes
LicenseFile={#StagingDir}\LICENSE
PrivilegesRequired=admin
OutputBaseFilename=ConnectionWatcher-Setup-{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=ConnectionWatcher
CloseApplications=yes

[Tasks]
Name: "service"; Description: "Install and start the ConnectionWatcher service on this machine"
Name: "tray"; Description: "Start the tray companion when you sign in"

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Run]
; The install steps themselves run from [Code] (CurStepChanged), because a
; plain [Run] entry cannot fail the wizard on a non-zero exit code.
Filename: "{code:DashboardUrl}"; Description: "Open the ConnectionWatcher dashboard"; \
    Flags: postinstall shellexec skipifsilent

[UninstallRun]
Filename: "{app}\conwatch.exe"; Parameters: "service stop"; RunOnceId: "StopService"; Flags: runhidden waituntilterminated
Filename: "{app}\conwatch.exe"; Parameters: "service uninstall"; RunOnceId: "RemoveService"; Flags: runhidden waituntilterminated
Filename: "{app}\conwatch.exe"; Parameters: "tray uninstall"; RunOnceId: "RemoveTray"; Flags: runhidden waituntilterminated runasoriginaluser

[Code]
var
  PortPage: TInputQueryWizardPage;
  UrlPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  PortPage := CreateInputQueryPage(wpSelectTasks,
    'Dashboard port', 'Which port should the dashboard listen on?',
    'The service listens on this TCP port on every interface and the firewall is opened for it. 8080 is the usual choice.');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := '8080';

  UrlPage := CreateInputQueryPage(PortPage.ID,
    'ConnectionWatcher server', 'Where is your ConnectionWatcher server?',
    'The tray companion needs the address of the machine running the service (for example http://nas:8080).');
  UrlPage.Add('Server URL:', False);
  UrlPage.Values[0] := 'http://localhost:8080';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = PortPage.ID then
    Result := not WizardIsTaskSelected('service');
  if PageID = UrlPage.ID then
    Result := WizardIsTaskSelected('service') or not WizardIsTaskSelected('tray');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    Port := StrToIntDef(PortPage.Values[0], 0);
    if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('Enter a port between 1 and 65535.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function DashboardUrl(Param: string): string;
begin
  if WizardIsTaskSelected('service') then
    Result := 'http://localhost:' + PortPage.Values[0]
  else
    Result := UrlPage.Values[0];
end;

{ Runs conwatch with the given arguments (elevated, as the wizard is) and
  aborts the wizard with a message when it fails. Exit codes: see the
  conwatch design spec (1 usage, 2 config, 3 not installed, 4 control). }
procedure RunConwatch(const Args: string);
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{app}\conwatch.exe'), Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('Could not start conwatch.exe ' + Args);
  if ResultCode <> 0 then
    RaiseException('conwatch ' + Args + ' failed (exit ' + IntToStr(ResultCode) + '). ' +
      'Open a terminal in ' + ExpandConstant('{app}') + ' and run: conwatch service status');
end;

{ Same, as the signed-in user: the tray autostart lives in that user's HKCU. }
procedure RunConwatchAsUser(const Args: string);
var
  ResultCode: Integer;
begin
  if not ExecAsOriginalUser(ExpandConstant('{app}\conwatch.exe'), Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('Could not start conwatch.exe ' + Args);
  if ResultCode <> 0 then
    RaiseException('conwatch ' + Args + ' failed (exit ' + IntToStr(ResultCode) + ').');
end;

{ ssInstall: stop what is running so files can be replaced (upgrade from the
  two-folder layout included). ssPostInstall: the install steps, in order,
  then remove the old folders — after `service install` has read the old
  contray.json from them. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ConTray.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM conwatch-tray.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop ConnectionWatcher', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('service') then
    begin
      RunConwatch('service install --port ' + PortPage.Values[0] + ' --no-start');
      RunConwatch('service start');
    end;
    if WizardIsTaskSelected('tray') then
    begin
      if WizardIsTaskSelected('service') then
        RunConwatchAsUser('tray install')
      else
        RunConwatchAsUser('tray install --url "' + UrlPage.Values[0] + '"');
      ExecAsOriginalUser(ExpandConstant('{app}\conwatch-tray.exe'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
    DelTree(ExpandConstant('{app}\Watcher'), True, True, True);
    DelTree(ExpandConstant('{app}\Tray'), True, True, True);
    DelTree(ExpandConstant('{app}\deploy'), True, True, True);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Also delete the measurement data and config in' + #13#10 +
              ExpandConstant('{commonappdata}\DSoftCo\ConnectionWatcher') + '?' + #13#10#13#10 +
              'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{commonappdata}\DSoftCo\ConnectionWatcher'), True, True, True);
  end;
end;
```

- [x] **Step 5: Workflows**

`.github/workflows/release.yml`: in the `installer` job's upload step and the `release` job's `files`, replace the single pattern with both:

```yaml
      - uses: actions/upload-artifact@v4
        with:
          name: installer
          path: |
            dist/ConnectionWatcher-Setup-*.exe
            dist/ConnectionWatcher-*-win-x64.zip
          if-no-files-found: error
```

```yaml
        with:
          files: |
            dist/ConnectionWatcher-Setup-*.exe
            dist/ConnectionWatcher-*-win-x64.zip
```

and in the release `body`, replace the Windows paragraph with:

```yaml
            **Windows:** `ConnectionWatcher-Setup-${{ steps.ver.outputs.version }}.exe` (wizard) or
            `ConnectionWatcher-${{ steps.ver.outputs.version }}-win-x64.zip` (unzip, then `conwatch service install`).
            Neither is code-signed yet, so SmartScreen will ask you to confirm.
            Upgrading from 0.9: the installer replaces `ConWatch.exe`/`ConTray.exe` with `conwatch.exe`/`conwatch-tray.exe`,
            re-registers the service, and moves your tray URL into the new config file automatically.
```

`.github/workflows/ci.yml`: after the `ConWatch.Tests` line add:

```yaml
      - run: dotnet test Tests/ConConfig.Tests -c Debug
      - run: dotnet test Tests/ConWatchCli.Tests -c Debug
```

- [x] **Step 6: Build the installer and the zip locally**

Run: `.\Build-Installer.ps1`
Expected: `staging\app` contains `conwatch.exe`, `conwatch-tray.exe`, `ConWatch.dll`, `wwwroot\`, `appsettings.json`, `README.txt`, `LICENSE`, and no `ConWatch.exe`; `dist\` has the Setup exe and the zip. Record the folder size and file count in the report (the design expects roughly the size of one of the old folders, about 125 MB, not two).

Run: `& staging\app\conwatch.exe --help` and `& staging\app\conwatch.exe config show`
Expected: help; four `(default)` lines (or your machine's existing values).

- [x] **Step 7: Commit**

```powershell
git add -A Build-Installer.ps1 deploy/windows .github/workflows Build-Tray.ps1 Build-Watcher.ps1 Publish-Tray.ps1 Publish-Watcher.ps1 Install-Tray.ps1 Install-Watcher.ps1 Uninstall-Tray.ps1 Uninstall-Watcher.ps1
git commit -m "build: one staging folder, thinned Inno wizard over conwatch, zip artifact; delete the PowerShell install glue"
```

---

### Task 10: Docs

**Files:**
- Modify: `README.md` (lines 36–39 and the whole "Running unattended" section up to "**Reverse proxy / HTTPS**"), `CONTRIBUTING.md` (Dev setup bullet about ConTray; Tests block; Releases paragraph)

- [x] **Step 1: README quick-start pointer (replace the two sentences that mention `Install-Watcher.ps1`)**

```markdown
Windows installers ship with each release on the
[Releases page](https://github.com/mdraughn/ConnectionWatcher/releases): a
Setup wizard and a zip. Either way the result is one folder with the
`conwatch` command — see [Running unattended](#running-unattended).
```

- [x] **Step 2: README "Running unattended" section (replace everything from `## Running unattended` up to, not including, `**Reverse proxy / HTTPS**`)**

````markdown
## Running unattended

**Wizard** — run `ConnectionWatcher-Setup-<version>.exe`. It installs to
`C:\Program Files\DSoftCo\ConnectionWatcher`, asks for the dashboard port,
registers the service, opens the firewall, starts the tray companion at
sign-in, and opens the dashboard. Uncheck "Install and start the service"
on a machine that only needs the tray, and it asks for the server URL instead.

**Zip** — unzip `ConnectionWatcher-<version>-win-x64.zip` anywhere and, from
a terminal in that folder:

```powershell
conwatch service install --port 8080
conwatch tray install
conwatch service status
```

The first command asks for administrator rights once. Both installs are the
same folder and the same commands; the wizard just clicks them for you.

**The `conwatch` command** does everything else:

| Command | What it does |
|---|---|
| `conwatch serve` | Run the watcher in the foreground (this is also what the service runs). |
| `conwatch service install\|uninstall\|start\|stop` | Manage the Windows service (elevates once). |
| `conwatch service status` | Installed? Running? Which port and data folder? Does the dashboard answer? |
| `conwatch config show` | Every setting and where it came from. |
| `conwatch config set listen-url\|data-dir\|server-url <value>` | Change one; the service reads it on its next start. |
| `conwatch tray` / `conwatch tray install\|uninstall` | Start the tray now / at sign-in. |

Settings live in `%ProgramData%\DSoftCo\ConnectionWatcher\conwatch.json`
(listen URL, data folder, the tray's server URL) with a per-user override in
`%AppData%\DSoftCo\ConnectionWatcher\tray.json` written by the tray's
Settings dialog or `conwatch config set server-url <url> --user`. Data stays
in `%ProgramData%\DSoftCo\ConnectionWatcher\data`. Uninstalling never deletes
either without asking.

**Tray companion** — `conwatch-tray.exe`: green/orange/red/gray at a glance;
double-click opens the dashboard; right-click for History, the ISP report,
Admin, and **Settings** (server URL with test-before-save, back to the
installed default, toast notifications). It runs on machines that are not
running the server too: point it at the NAS with `conwatch tray install --url http://nas:8080`.

**Upgrading from 0.9** — the wizard replaces `ConWatch.exe` and `ConTray.exe`
with `conwatch.exe` and `conwatch-tray.exe`, re-registers the service, moves
your tray URL from `contray.json` into the new config file, and removes the
old `Watcher\` and `Tray\` folders. The `Install-*.ps1` scripts and
`deploy/windows/*.ps1` are gone.

````

- [x] **Step 3: CONTRIBUTING**

Replace the ConTray bullet under "Dev setup":

```markdown
- `Programs/ConWatchCli` is the `conwatch` command (help, `serve`, service,
  config, tray); `Programs/ConWatchTray` is the tray companion and is
  Windows-only. Run the command from source with
  `dotnet run --project Programs/ConWatchCli -- --help`. Everything else is
  cross-platform.
```

In the Tests block add two lines after `dotnet test Tests/ConWatch.Tests -c Debug`:

```bash
dotnet test Tests/ConConfig.Tests -c Debug
dotnet test Tests/ConWatchCli.Tests -c Debug
```

In Releases, replace `builds `ConnectionWatcher-Setup-1.2.3.exe` on a Windows runner, and creates a GitHub Release with generated notes and the installer attached.` with `builds `ConnectionWatcher-Setup-1.2.3.exe` and `ConnectionWatcher-1.2.3-win-x64.zip` on a Windows runner (`Build-Installer.ps1`), and creates a GitHub Release with generated notes and both attached.`

- [x] **Step 4: Check for stale references and commit**

Run: `grep -rn "Install-Watcher\|Install-Tray\|install-service\|configure-tray\|ConTray\.exe\|contray\.json" README.md CONTRIBUTING.md Programs Libraries Tests --include=*.md --include=*.cs --include=*.cshtml`
Expected: only `LegacyInstall.cs`, `TraySettings.cs`, and the README "Upgrading from 0.9" paragraph.

```powershell
git add README.md CONTRIBUTING.md
git commit -m "docs: install section around the conwatch command, zip, config files; contributing notes"
```

---

### Task 11: Manual verification on this machine

**Files:** none (verification only; the spec §2.1 already states that `serve` resolves config and passes host arguments).

- [x] **Step 1: Full suite**

Run: `dotnet test ConnectionWatcher.sln -c Debug` and `cd Programs/ConWatch/ClientApp; npx vitest run`
Expected: every project green, 0 warnings in the build output. Record counts in the report.

- [x] **Step 2: Manual checklist (record each line's result in the report; do not skip)**

Status 2026-09-23: all eleven items run with Mark at the keyboard, plus the port-change pass (8080→9090) and an uninstall with the tray running. Findings fixed in fix wave 2 (commits b152c0e..4fa33e6): `config set listen-url` now keeps `serverUrl` on the same port and points at `service install --port`; `conwatch tray` reports an already-running tray (including the 0.9 `ConTray.exe`, which shares the mutex); the uninstaller removes the empty program folder and the data-deletion prompt spells out its consequence. Re-tested after the fixes: install, already-running tray message, uninstall answering No keeps config and database.

From an **unelevated** PowerShell in `staging\app` after `.\Build-Installer.ps1`:

1. `.\conwatch.exe service install --port 8080` → exactly one UAC prompt; output echoed in this window ends with the four summary lines; exit code 0.
2. `.\conwatch.exe service status` → `Running (auto-start)`, `Binary: "...\staging\app\conwatch.exe" serve`, `Health: Healthy` (or `EngineDown` on a machine without configured targets; either is fine, the point is the HTTP answer). Exit 0.
3. `Get-Content $env:ProgramData\DSoftCo\ConnectionWatcher\conwatch.json` → `listenUrl`, `dataDir`, `serverUrl` present.
4. Browser at `http://localhost:8080` shows the dashboard with CSS and the React islands (proves static web assets published under the CLI).
5. `.\conwatch.exe config set listen-url http://0.0.0.0:8081` → UAC once; then `service stop`, `service start`, `service status` → Listen shows 8081 and Health answers on 8081 (the firewall rule is only re-made on install, so if the dashboard is unreachable from another machine on 8081 that is expected; localhost works).
6. `.\conwatch.exe tray install` → no UAC; `Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run` shows `ConnectionWatcherTray`. `.\conwatch.exe tray` starts the icon; a second `.\conwatch.exe tray` does not start a second icon.
7. Tray Settings dialog: set a custom URL, OK → `Get-Content $env:APPDATA\DSoftCo\ConnectionWatcher\tray.json` shows it. `.\conwatch.exe config show` shows `server-url` from the user file.
8. `.\conwatch.exe tray uninstall`, `.\conwatch.exe service uninstall` → UAC once for the latter; `service status` → `not installed`, exit 3; `Get-NetFirewallRule -DisplayName ConnectionWatcher` → none; Start Menu shortcut gone; `conwatch.json` still present.
9. `.\conwatch.exe service start` when not installed → exit 3 with the hint. `.\conwatch.exe bogus` → exit 1. `.\conwatch.exe` → help.
10. Run `dist\ConnectionWatcher-Setup-<v>.exe` with both tasks checked → service running, tray icon up, dashboard opens. Uninstall from Settings → prompt about ProgramData; answer No; folder removed; `conwatch.json` and data kept.
11. Upgrade path: if a 0.9 install exists (or install the previous Setup exe first), run the new Setup over it → after install, `Watcher\` and `Tray\` are gone, the service binary path is `conwatch.exe serve`, and `serverUrl` equals what `Tray\contray.json` held.

- [ ] **Step 3: Record the results**

Every numbered line above goes into the task report with its observed result. A line that fails is a finding for the final review, not a reason to skip the rest.

---

## Self-review notes (written at plan time)

- Spec §2.5 says the merged publish shares runtime files: Task 9 publishes both projects into one folder and removes the stray `ConWatch.exe` apphost.
- Spec §3.5 migration: Task 5 (`LegacyInstall`, once, only without a machine file) and Task 8 (`LegacyTrayFile` fallback in the tray).
- Spec §5.1 output capture: Task 4 `Program.cs` strips `--__output`; Task 6 `WindowsElevation` adds it.
- Spec §5.2 Start Menu shortcut is a `.url` file here instead of a `.lnk` (no COM); same folder, same name stem; the old `.lnk` is removed.
- Spec §7.1 `[InstallDelete]` is replaced by `ssPostInstall` `DelTree` because the migration in `[Run]` must read the old folders first.
- Not covered by an automated test, by design: `WindowsServiceManager`, `WindowsElevation`, `WindowsTrayAutostart`, the tray UI. Task 11 covers them by hand.
