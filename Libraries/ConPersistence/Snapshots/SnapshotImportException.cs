namespace ConPersistence.Snapshots;

/// <summary>
/// An import failed for a reason other than the snapshot being refused up front
/// (an unsupported schema or a config that fails validation stay plain
/// <see cref="InvalidOperationException"/>s). Says which side of the config commit it
/// happened on: before, the node's config is unchanged; after, it already holds the
/// snapshot's config and only the measurement data is incomplete.
/// </summary>
public sealed class SnapshotImportException(bool configCommitted, string message, Exception inner)
    : Exception(message, inner)
{
    public bool ConfigCommitted { get; } = configCommitted;
}
