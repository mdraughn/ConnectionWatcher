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
