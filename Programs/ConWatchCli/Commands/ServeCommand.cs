using System.CommandLine;
using ConConfig;
using ConWatchCli.Services;

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
        var machine = ConfigFile.TryRead(s.Paths.MachineFile, out var warning, out var unreadable);
        if (warning is not null && unreadable)
            throw new ConfigException($"{s.Paths.MachineFile} could not be read: {warning}");
        if (warning is not null)
            s.Err.WriteLine($"warning: ignoring malformed config {warning}");
        var listen = ConfigResolver.ListenUrl(s.Paths, machine, urlFlag, s.Env(ConfigResolver.EnvListenUrl));
        var dataDir = ConfigResolver.DataDir(s.Paths, machine, dataFlag, s.Env(ConfigResolver.EnvDataDir));
        return ["--urls", listen.Value, "--Persistence:DbPath", Path.Combine(dataDir.Value, "conwatch.db")];
    }
}
