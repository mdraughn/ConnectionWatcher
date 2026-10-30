namespace ConEngine.Topology;

public static class VerdictStateExtensions
{
    /// <summary>True unless the verdict is LinkDown or Unknown.</summary>
    public static bool IsReachable(this VerdictState v) =>
        v is not (VerdictState.LinkDown or VerdictState.Unknown);
}
