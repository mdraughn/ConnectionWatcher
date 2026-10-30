namespace ConWatch;

/// <summary>
/// The one place that builds the status snapshot for the current engine, shared by
/// <c>/status</c>, the dashboard and the MCP status tool. <see cref="StatusBroadcaster"/>
/// does not use it: the host takes the broadcaster in its constructor (a DI cycle), and the
/// broadcaster builds for the specific engine it was handed.
/// </summary>
public sealed class StatusSource(WatcherHostService host, ConPersistence.Warnings.ModelWarningLedger warnings,
    ConInfrastructure.NameResolution.DnsLookupNames lookupNames)
{
    /// <summary>The live snapshot, or null when there is no engine (archive mode or not started yet).</summary>
    public StatusSnapshot? Live() => StatusSnapshotBuilder.Build(host.Engine, warnings, lookupNames.Current);

    /// <summary>The live snapshot; the disabled snapshot in archive mode; null only when monitoring
    /// is enabled but the engine is not running yet.</summary>
    public StatusSnapshot? Current() => Live() ?? (host.EngineEnabled ? null : StatusSnapshot.Disabled);
}
