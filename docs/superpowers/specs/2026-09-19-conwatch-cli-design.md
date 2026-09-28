# Single `conwatch` command: design

Date: 2026-09-19. Baseline: master `f165326`. Status: approved in chat section by section; awaiting review of this file.

## 1. Goal

Replace the two-folder Windows install (`Watcher\ConWatch.exe` plus `Tray\ConTray.exe`, each with its own copy of the runtime, glued together by PowerShell scripts and a sidecar `contray.json`) with one program folder, one command, and one config file:

- `conwatch` prints help. `conwatch serve` runs the watcher. `conwatch service …` installs, starts, stops, and reports on the Windows service. `conwatch config …` reads and writes the config file. `conwatch tray …` launches or auto-starts the tray.
- The tray reads its server address from the standard app-data locations, not from a file beside the exe.
- Everything an administrator needs can be done from a terminal; the Inno wizard becomes a thin front end over the same commands, and a plain zip is offered beside it.
- One copy of the .NET runtime in the install.

Scope is the install plumbing and the tray's server address. No live-control REST client, no changes to watcher settings, profiles, or notifier config (those stay in SQLite). The Docker image and the Synology deployment are untouched.

## 2. Projects and build

### 2.1 `Programs/ConWatch` (existing, cross-platform web host)

Stays `net10.0`, output type Exe, referenced by tests and the Dockerfile as today. One refactor: `Program.cs` becomes a thin `Main` over a new static class:

```csharp
namespace ConWatch;
public static class ConWatchApp
{
    /// Builds the host, prepares the database, and runs until the token fires or the host stops.
    public static Task<int> RunAsync(string[] args, CancellationToken ct = default);
}
```

`RunAsync` contains everything `Program.cs` does now (the `WebApplicationOptions` with the SCM content-root pin, `AddWindowsService`, the 25 s `ShutdownTimeout`, the service registrations, `DatabaseStartup.PrepareAsync`, the middleware and endpoint map). `AddSystemd()` is added beside `AddWindowsService()`; both are inert outside their host. `WebApplicationFactory<Program>` in `ConWatch.Tests` keeps working because `Program` still exists as the entry point.

The host does not read the config file itself. `conwatch serve` resolves the listen URL and data folder (flag > `CONWATCH_*` env > machine file > default) and passes them to `RunAsync` as ordinary host arguments (`--urls`, `--Persistence:DbPath`), so the service binary path stays `conwatch.exe serve` and the container, which never runs through the CLI, is unaffected.

### 2.2 `Libraries/ConConfig` (new, tiny)

Holds the config model and reader so the tray does not reference the CLI and the CLI does not reference Windows Forms:

```csharp
namespace ConConfig;
public sealed record ConWatchConfig(string? ListenUrl, string? DataDir, string? ServerUrl, bool? Notifications);
public enum ConfigSource { Flag, Environment, User, Machine, Default }
public sealed record Resolved<T>(T Value, ConfigSource Source, string? Path);

public sealed class ConfigPaths   // all overridable for tests
{
    public string MachineFile { get; }   // %ProgramData%\DSoftCo\ConnectionWatcher\conwatch.json
    public string UserFile { get; }      // %AppData%\DSoftCo\ConnectionWatcher\tray.json
    public string DefaultDataDir { get; } // %ProgramData%\DSoftCo\ConnectionWatcher\data
}

public static class ConfigFile
{
    public static ConWatchConfig? TryRead(string path, out string? error); // missing or malformed → null
    public static void WriteAtomic(string path, ConWatchConfig config);    // temp file + File.Move(overwrite)
}

public static class ConfigResolver
{
    public static Resolved<string> ListenUrl(ConfigPaths p, ConWatchConfig? machine, string? flag, string? env);
    public static Resolved<string> DataDir(ConfigPaths p, ConWatchConfig? machine, string? flag, string? env);
    public static Resolved<string> ServerUrl(ConfigPaths p, ConWatchConfig? user, ConWatchConfig? machine);
    public static string ListenToServerUrl(string listenUrl); // 0.0.0.0, +, * → localhost; strips trailing slash
}
```

Target `net10.0`, no package references beyond the BCL.

### 2.3 `Programs/ConWatchCli` (new, the command)

- Assembly name `conwatch`, output type Exe (console subsystem), `net10.0`, no Windows Forms.
- References `ConWatch`, `ConConfig`, `System.CommandLine`, and `System.ServiceProcess.ServiceController` (Windows-only package; every call sits behind `OperatingSystem.IsWindows()`).
- Folders: `Commands/` (one class per subcommand group), `Services/` (`IServiceManager`, `WindowsServiceManager`, `UnsupportedServiceManager`, `IElevation`, `WindowsElevation`, `IHealthProbe`), `Tray/` (`TrayLauncher`, `TrayAutostart`).
- `Program.Main` builds the root command from the grammar in section 4, injects the real implementations, and returns the exit code.

### 2.4 `Programs/ConWatchTray` (new home for the tray)

- Assembly name `conwatch-tray`, output type WinExe (GUI subsystem), `net10.0-windows`, `UseWindowsForms`.
- `Programs/ConTray/*` moves here unchanged except `TraySettings.cs`, which is replaced by a thin wrapper over `ConConfig` (section 3.3). References `ConConfig` only. Never references the CLI.
- `Programs/ConTray` is deleted.

Why two exes: a console-subsystem exe launched from a shortcut or at login opens a console window; a GUI-subsystem exe launched from a terminal returns immediately and cannot print help. Two apphost stubs sharing one runtime folder cost about half a megabyte and avoid the hidden-console trick.

### 2.5 Build and publish

`Build-Installer.ps1` publishes `ConWatchCli` and `ConWatchTray` self-contained `win-x64` into the same `staging/app` folder (same RID, same SDK, same build; identical shared files overwrite each other). It then compiles the Inno script and zips `staging/app` plus `README.txt` into `dist/ConnectionWatcher-<version>-win-x64.zip`. Version stamping through `/p:Version` is unchanged.

Deleted: root `Build-Tray.ps1`, `Build-Watcher.ps1`, `Publish-Tray.ps1`, `Publish-Watcher.ps1`, `Install-Tray.ps1`, `Install-Watcher.ps1`, `Uninstall-Tray.ps1`, `Uninstall-Watcher.ps1`; `deploy/windows/common.ps1`, `install-service.ps1`, `uninstall-service.ps1`, `install-tray.ps1`, `uninstall-tray.ps1`, `configure-tray.ps1`. Kept: `Build-Installer.ps1`, `Build-Images.ps1`, `Get-NasSnapshot.ps1`, `deploy/windows/installer.iss`, the Dockerfile, the compose file.

The solution file gains the three new projects and the test project from section 8 and drops `ConTray`.

## 3. Config file and precedence

### 3.1 Files

Machine file, `%ProgramData%\DSoftCo\ConnectionWatcher\conwatch.json`, written only by elevated commands:

```json
{
  "listenUrl": "http://0.0.0.0:8080",
  "dataDir": "C:\\ProgramData\\DSoftCo\\ConnectionWatcher\\data",
  "serverUrl": "http://localhost:8080"
}
```

Per-user file, `%AppData%\DSoftCo\ConnectionWatcher\tray.json`, never elevated. Same shape, but only `serverUrl` and `notifications` are honored:

```json
{ "serverUrl": "http://nas:8080", "notifications": true }
```

Unknown properties are ignored on read and preserved on write.

### 3.2 Precedence

Service (`serve`): command-line flag, then `CONWATCH_*` environment variable, then the machine file, then the default. Defaults: `listenUrl` `http://0.0.0.0:8080`, `dataDir` `%ProgramData%\DSoftCo\ConnectionWatcher\data` on Windows and `./data` elsewhere.

Tray: per-user file, then machine file `serverUrl`, then machine file `listenUrl` through `ListenToServerUrl`, then `http://localhost:8080`. `notifications` defaults to true.

### 3.3 Tray settings wrapper

`ConWatchTray.TraySettings` keeps the members `Program.cs` and `SettingsForm.cs` use today (`EffectiveUrl`, `EffectiveSource`, `HasOverride`, `InstalledDefaultUrl`, `NotificationsEnabled`, `SetOverride`, `ClearOverride`, `SetNotifications`) and implements them over `ConfigFile` and `ConfigResolver`. "Installed default" now means the machine file. `EffectiveSource` reports `custom`, `installed default`, or `built-in fallback` as before.

### 3.4 Validation and errors

`config set` validates before writing: `listen-url` and `server-url` must parse as absolute `http` or `https` URIs; `data-dir` must be an absolute path that exists or can be created. Writes go through `WriteAtomic`. A missing file is silently absent. A malformed file is reported once with its path and the parse error, then treated as absent; it is never fatal for `serve` or the tray.

### 3.5 Migration

Runs inside `service install`, once, before writing the machine file, only when no machine file exists:

- If the previous layout's `<install root>\Watcher\contray.json` or `<install root>\Tray\contray.json` exists with a `url`, seed `serverUrl` from it.
- If a service named `ConnectionWatcher` exists whose binary path contains `ConWatch.exe`, parse the port from its `--urls` argument to seed `listenUrl` (only when `--port`/`--url` was not given), then delete that registration before creating the new one.

The tray reads the old per-user file `%AppData%\DSoftCo\ConnectionWatcher\contray-settings.json` (`url`, `notifications`) when `tray.json` is absent, rewrites it as `tray.json` on first save, and leaves the old file in place.

## 4. Command grammar

```text
conwatch                                  help (same as --help); exit 0
conwatch version                          version and build stamp
conwatch serve [--url <u>] [--data <dir>] run the watcher: under SCM when IsWindowsService(), else foreground with Ctrl+C
conwatch service install [--port <n> | --url <u>] [--data <dir>] [--no-start]   elevated
conwatch service uninstall                                                        elevated
conwatch service start | stop                                                     elevated
conwatch service status                   installed?, SCM state, binary path, health
conwatch config show                      every effective value with its source and file path
conwatch config set listen-url <u>        elevated; writes machine file
conwatch config set data-dir <dir>        elevated; writes machine file
conwatch config set server-url <u> [--user]   machine file (elevated) or per-user file
conwatch tray                             launch conwatch-tray.exe and return
conwatch tray install [--url <u>]         register autostart for this user; --url writes the per-user file
conwatch tray uninstall                   remove autostart for this user
```

`--url` and `--port` on `service install` are mutually exclusive; `--port <n>` means `http://0.0.0.0:<n>`. With neither, the port is probed from 8080 upward for a free TCP listener, as `install-service.ps1` does today, and the result is pinned in the machine file.

Exit codes: 0 success, 1 usage error (System.CommandLine's parse failure), 2 config invalid, 3 service or tray exe not installed, 4 service control failed, 5 elevation declined, 130 cancelled with Ctrl+C.

Help text is System.CommandLine's default with a one-paragraph description on the root command naming the config file locations.

## 5. Elevation and service control (Windows)

### 5.1 Elevation

`IElevation { bool IsElevated { get; } Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct); }`.

Commands marked elevated call `IsElevated` first. If false, `RelaunchElevatedAsync` starts the same exe with the same arguments plus `--__output <tempfile>` using `ProcessStartInfo { UseShellExecute = true, Verb = "runas" }`, waits for exit, echoes the temp file to the caller's stdout, deletes it, and returns the child's exit code. The child writes its console output to that file when the hidden option is present. `Win32Exception` with `NativeErrorCode == 1223` (the user cancelled UAC) returns 5 with the message `This command needs administrator rights.` The Inno wizard already runs elevated, so no second prompt appears during setup.

### 5.2 `IServiceManager`

```csharp
public interface IServiceManager
{
    Task InstallAsync(ServiceSpec spec, CancellationToken ct);   // idempotent: reconfigures in place
    Task UninstallAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<ServiceState> StatusAsync(CancellationToken ct);          // NotInstalled | Stopped | Running | Other(string)
}
public sealed record ServiceSpec(string ExePath, string ListenUrl, string DataDir);
```

`WindowsServiceManager` does what `install-service.ps1` does today, in this order: stop and delete an existing `ConnectionWatcher` service; create it with `sc.exe create ConnectionWatcher binPath= "\"<exe>\" serve" start= auto DisplayName= "ConnectionWatcher"`, description `Home internet connectivity monitor (dashboard at <serverUrl>)`; `sc.exe failure ConnectionWatcher reset= 86400 actions= restart/5000/restart/30000/restart/60000`; replace the firewall rule named `ConnectionWatcher` with `netsh advfirewall firewall add rule name=ConnectionWatcher dir=in action=allow protocol=TCP localport=<port> profile=private,domain`; write the Start Menu shortcut `ConnectionWatcher Dashboard.lnk` pointing at `serverUrl` (via the `WScript.Shell` COM object, as today); start the service unless `--no-start`. Uninstall reverses all of it and leaves data and config in place. Arguments are passed to `sc.exe` as an argument list, not a shell string, so embedded quotes survive.

The binary path carries no configuration. `serve` reads the machine file, so `config set` takes effect at the next `service stop` / `service start` without re-registering.

`UnsupportedServiceManager` (every non-Windows OS) throws `PlatformNotSupportedException("service control is not implemented on this platform")`, mapped to exit 4. This is the slot a systemd implementation fills later.

### 5.3 `service status`

Prints:

```text
Service:  ConnectionWatcher — Running (auto-start)
Binary:   "C:\Program Files\DSoftCo\ConnectionWatcher\conwatch.exe" serve
Listen:   http://0.0.0.0:8080  (machine file)
Data:     C:\ProgramData\DSoftCo\ConnectionWatcher\data  (machine file)
Health:   Healthy  (http://localhost:8080/api/v1/health, 41 ms)
```

`IHealthProbe.ProbeAsync(serverUrl, timeout 3 s)` returns the `status` word from `/api/v1/health` or a one-line failure (`refused`, `timed out`, `HTTP 503`). Exit code stays 0 whatever the health says; only "not installed" returns 3. This is the CLI's only HTTP call and it is read-only.

## 6. The tray

- `conwatch-tray.exe` takes no arguments. It resolves its URL per section 3.2, keeps the existing named mutex `ConnectionWatcherTray` for single instance (a second launch exits 0), and keeps the existing menu, polling, grey-icon-on-unreachable behavior, and notifications toggle.
- The existing "Settings…" dialog (`SettingsForm`) writes the per-user file. No change to its layout; only its persistence target moves.
- Autostart uses the existing per-user Run key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ConnectionWatcherTray` with the full exe path, written and removed by `conwatch tray install | uninstall`. Not elevated.
- `conwatch tray` starts `conwatch-tray.exe` from `AppContext.BaseDirectory` with `UseShellExecute = false` and returns 0, or 3 with `conwatch-tray.exe not found beside conwatch.exe` if it is missing.

## 7. Installer and zip

### 7.1 Inno wizard (`deploy/windows/installer.iss`)

- Same `AppId`, `AppName`, `DefaultDirName`, and `OutputBaseFilename`. `PrivilegesRequired=admin` stays.
- One `[Files]` entry: `staging\app\*` → `{app}`. No `deploy` folder, no `Watcher`/`Tray` subfolders.
- `[Components]` and `[Types]` are replaced by two `[Tasks]`, both checked by default: `service` ("Install and start the ConnectionWatcher service on this machine") and `tray` ("Start the tray companion when you sign in").
- The port page (a number, default 8080) shows when `service` is checked; the server URL page shows when `service` is unchecked and `tray` is checked, as the current `ShouldSkipPage` logic does.
- `[Run]`: `{app}\conwatch.exe service install --port <n> --no-start` then `{app}\conwatch.exe service start` when `service` is checked; `{app}\conwatch.exe tray install [--url <u>]` then `{app}\conwatch-tray.exe` when `tray` is checked; the postinstall "open the dashboard" entry stays. All `runhidden`, and a non-zero exit code fails the wizard with the CLI's message shown.
- `[UninstallRun]`: `conwatch service stop`, `conwatch service uninstall`, `conwatch tray uninstall`, each with `RunOnceId`. Data and machine config stay; the existing uninstall behavior of never deleting `ProgramData` is kept, with an `[Code]` prompt after uninstall offering to delete `%ProgramData%\DSoftCo\ConnectionWatcher`.
- Upgrade in place: `[InstallDelete]` removes `{app}\Watcher` and `{app}\Tray` after `service install` has run its migration (section 3.5), which reads them first. Because `service install` stops and recreates the service, the previous `ConWatch.exe` registration is replaced without a manual step. Inno's `CloseApplications` handles a running tray.

### 7.2 Zip

`dist/ConnectionWatcher-<version>-win-x64.zip` contains the same `app` folder contents at the root plus `README.txt`:

```text
ConnectionWatcher <version> — manual install

1. Unzip anywhere (for example C:\Program Files\DSoftCo\ConnectionWatcher).
2. From a terminal in that folder:
     conwatch service install --port 8080
     conwatch tray install
     conwatch service status
   The first command asks for administrator rights once.

Config:  %ProgramData%\DSoftCo\ConnectionWatcher\conwatch.json
Data:    %ProgramData%\DSoftCo\ConnectionWatcher\data
Tray:    %AppData%\DSoftCo\ConnectionWatcher\tray.json  (per user)

Remove:  conwatch service uninstall ; conwatch tray uninstall ; delete the folder.
```

### 7.3 Release workflow

`release.yml` uploads `dist/ConnectionWatcher-Setup-*.exe` and `dist/ConnectionWatcher-*-win-x64.zip` and lists both in the release body. The edge channel and `publish-image.yml` are unchanged. Code signing remains a later item.

## 8. Tests

New project `Tests/ConWatchCli.Tests` (xUnit, like the others). Everything runs in-process against fakes; no test touches SCM, UAC, the firewall, the registry, or `%ProgramData%`.

- Grammar: `conwatch` with no arguments prints help and returns 0; each subcommand calls the expected `IServiceManager` / `TrayAutostart` / `TrayLauncher` method with the expected arguments; `--url` with `--port` is a usage error (1).
- Elevation: an elevated command with `IsElevated == false` calls `RelaunchElevatedAsync` with the same arguments and returns its code; a cancelled relaunch returns 5; `IsElevated == true` runs inline.
- Config: precedence for `config show` (flag over env over machine over default, and user over machine for `serverUrl`); `ListenToServerUrl` cases (`0.0.0.0`, `+`, `*`, IPv6 any, trailing slash); `config set` rejects a relative URL and a non-http scheme with exit 2 and does not write; `WriteAtomic` leaves no temp file and replaces the target; a malformed machine file yields a warning and defaults.
- Migration: seeds `serverUrl` from an old `contray.json` and `listenUrl` from an old `--urls` binary path exactly once, and not when a machine file already exists.
- Status: `service status` output for Running with a healthy probe, Stopped, NotInstalled (exit 3), and a probe that refuses or times out.
- Tray: `ConWatchTray.Tests` is not created; `TraySettings` resolution (user over machine over default, old `contray-settings.json` fallback) is covered in `ConConfig.Tests` or `ConWatchCli.Tests` through `ConfigResolver`.
- `ConWatch.Tests` is unchanged. Tests referencing `ConTray` move with the tray code.

Manual checklist before merge (in the plan, not automated): run `Build-Installer.ps1`; from an unelevated prompt in `staging\app`, run `conwatch service install --port 8080` and confirm exactly one UAC prompt; `conwatch service status` shows Running and Healthy; `conwatch tray install` then sign out and in, tray appears; `conwatch config set server-url http://localhost:8080 --user` and the tray picks it up on restart; `conwatch service uninstall`, `conwatch tray uninstall`; install the Setup exe over the current release layout and confirm the migration and the removal of `Watcher\` and `Tray\`.

## 9. Docs and release notes

- `README.md` install section: the wizard, the zip and its three commands, the config file locations, and `conwatch --help`.
- Release notes: `ConWatch.exe` and `ConTray.exe` are replaced by `conwatch.exe` and `conwatch-tray.exe`; `contray.json` is replaced by the config files above; the service is re-registered automatically on upgrade; the `deploy/windows/*.ps1` scripts are gone.
- `CONTRIBUTING.md` build section updated for the new projects.

## 10. Out of scope

Live control of the service through its REST API from the CLI; a Linux systemd implementation of `IServiceManager` (the interface and the `UnsupportedServiceManager` are the only Linux work here); changes to the container image; code signing.
