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
            if (s.TrayLauncher.RunningInstance() is { } running)
            {
                s.Out.WriteLine($"The tray is already running: {running}");
                if (running.Contains("ConTray.exe"))
                    s.Out.WriteLine("Close it (right-click, Exit) and run conwatch tray again to start the new tray.");
                return Task.FromResult(ExitCodes.Ok);
            }
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
        install.SetAction((pr, ct) => CliApp.Guarded(s, () =>
        {
            if (!File.Exists(s.TrayExePath))
            {
                s.Err.WriteLine($"{Path.GetFileName(s.TrayExePath)} not found beside conwatch.");
                return Task.FromResult(ExitCodes.NotInstalled);
            }
            if (pr.GetValue(url) is { } u)
            {
                if (!ConfigResolver.ValidateUrl(u, out var error)) throw new ConfigException(error!);
                var normalized = ConfigResolver.NormalizeServerUrl(u);
                CliConfig.Update(s, s.Paths.UserFile, current => (current ?? new ConWatchConfig()) with { ServerUrl = normalized });
                s.Out.WriteLine($"server-url = {normalized}  written to {s.Paths.UserFile}");
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
