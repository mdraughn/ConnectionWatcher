using System.CommandLine;
using System.CommandLine.Invocation;
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
        root.Subcommands.Add(ServiceCommands.Build(s, rawArgs));
        root.Subcommands.Add(ConfigCommands.Build(s, rawArgs));
        root.Subcommands.Add(TrayCommands.Build(s));
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

    /// <summary>Maps the typed exceptions to the exit-code table. A cancellation
    /// caused by <paramref name="ct"/> (Ctrl+C) is the user stopping us, not a failure.</summary>
    public static async Task<int> Guarded(CliServices s, Func<Task<int>> body, CancellationToken ct = default)
    {
        try
        {
            return await body();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            s.Err.WriteLine("Cancelled.");
            return ExitCodes.Cancelled;
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
        catch (Exception ex)
        {
            s.Err.WriteLine(ex.Message);
            return ExitCodes.ServiceControlFailed;
        }
    }

    /// <summary>Runs <paramref name="body"/> inline when elevated, else relaunches with the original arguments.</summary>
    public static Task<int> Elevated(CliServices s, string[] rawArgs, Func<Task<int>> body, CancellationToken ct) =>
        s.Elevation.IsElevated ? Guarded(s, body, ct) : Guarded(s, () => s.Elevation.RelaunchElevatedAsync(rawArgs, ct), ct);
}
