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
        var current = ConfigFile.TryRead(path, out var warn, out var unreadable) ?? new ConWatchConfig();
        if (warn is not null && unreadable)
            throw new ConfigException($"{path} could not be read: {warn}");
        if (warn is not null)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            s.Err.WriteLine($"warning: {path} was not valid JSON; moved to {path}.corrupt and replaced");
        }

        ConWatchConfig next;
        string stored;
        switch (key)
        {
            case "listen-url":
            case "server-url":
                if (!ConfigResolver.ValidateUrl(value, out var urlError)) throw new ConfigException(urlError!);
                stored = value.TrimEnd('/');
                next = key == "listen-url" ? current with { ListenUrl = stored } : current with { ServerUrl = stored };
                break;
            default: // data-dir
                if (!ConfigResolver.ValidateDataDir(value, out var dirError)) throw new ConfigException(dirError!);
                stored = value;
                next = current with { DataDir = stored };
                break;
        }
        ConfigFile.WriteAtomic(path, next);
        s.Out.WriteLine($"{key} = {stored}  written to {path}");
        if (!perUser && key != "server-url")
            s.Out.WriteLine("restart the service to pick this up: conwatch service stop, then conwatch service start.");
        return ExitCodes.Ok;
    }
}
