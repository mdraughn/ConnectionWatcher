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
            try
            {
                await child.WaitForExitAsync(ct);
                if (File.Exists(capture))
                    output.Write(await File.ReadAllTextAsync(capture, ct));
                return child.ExitCode;
            }
            finally
            {
                // Clean up the capture file even if the wait above is cancelled; the elevated
                // child is left running (we never kill it), it just writes to a file no one reads.
                try { if (File.Exists(capture)) File.Delete(capture); } catch { /* best effort */ }
            }
        }
    }
}
