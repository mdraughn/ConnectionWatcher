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
            string listen = "", dataDir = "", serverUrl = "";
            var written = CliConfig.Update(s, s.Paths.MachineFile, machine =>
            {
                var legacy = machine is null ? LegacyInstall.Detect(s.AppDir, status.BinaryPath) : new LegacyValues(null, null);
                listen = ResolveListen(s, machine, legacy, pr.GetValue(port), pr.GetValue(url));
                dataDir = ConfigResolver.DataDir(s.Paths, machine, pr.GetValue(data), s.Env(ConfigResolver.EnvDataDir)).Value;
                if (!ConfigResolver.ValidateUrl(listen, out var urlError)) throw new ConfigException(urlError!);
                if (!ConfigResolver.ValidateDataDir(dataDir, out var dirError)) throw new ConfigException(dirError!);
                var server = machine?.ServerUrl ?? legacy.ServerUrl ?? ConfigResolver.ListenToServerUrl(listen);
                return (machine ?? new ConWatchConfig()) with { ListenUrl = listen, DataDir = dataDir, ServerUrl = ConfigResolver.WithPortOf(server, listen) };
            });
            (listen, dataDir, serverUrl) = (written.ListenUrl!, written.DataDir!, written.ServerUrl!);
            // Before the service first starts, so the db and keys are born private.
            s.Lockdown.Lock(Path.GetDirectoryName(s.Paths.MachineFile)!, usersMayRead: true);
            LockDataDir(s, dataDir);

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

    /// <summary>
    /// The data folder holds conwatch.db (notification secrets, admin hash) and the
    /// data-protection keys, so only SYSTEM and Administrators may read it. A custom
    /// folder that already holds other files is left alone: rewriting its permissions
    /// could lock its owner out of their own files.
    /// </summary>
    private static void LockDataDir(CliServices s, string dataDir)
    {
        static bool IsOurs(string entry)
        {
            var name = Path.GetFileName(entry);
            return name == "keys" || name.StartsWith("conwatch.db", StringComparison.OrdinalIgnoreCase);
        }
        var isDefault = string.Equals(Path.GetFullPath(dataDir), Path.GetFullPath(s.Paths.DefaultDataDir), StringComparison.OrdinalIgnoreCase);
        if (!isDefault && Directory.Exists(dataDir) && !Directory.EnumerateFileSystemEntries(dataDir).All(IsOurs))
        {
            s.Err.WriteLine($"warning: left the permissions of {dataDir} unchanged because it holds other files; " +
                "make sure only administrators can read it (conwatch.db holds your notification secrets).");
            return;
        }
        s.Lockdown.Lock(dataDir, usersMayRead: false);
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
        cmd.SetAction((_, ct) => CliApp.Guarded(s, async () =>
        {
            if ((await s.Services.StatusAsync(ct)).State == ServiceState.NotInstalled)
            {
                s.Err.WriteLine("The ConnectionWatcher service is not installed. Run: conwatch service install");
                return ExitCodes.NotInstalled;
            }
            return await CliApp.Elevated(s, rawArgs, async () =>
            {
                await pick(s.Services)(ct);
                s.Out.WriteLine($"ConnectionWatcher service: {name} done.");
                return ExitCodes.Ok;
            }, ct);
        }, ct));
        return cmd;
    }

    private static Command Status(CliServices s)
    {
        var cmd = new Command("status", "Show whether the service is installed and running, its config, and whether the dashboard answers.");
        cmd.SetAction((parseResult, ct) => CliApp.Guarded(s, async () =>
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
        }, ct));
        return cmd;
    }
}
