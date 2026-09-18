namespace ConEngine;

/// <summary>
/// Something that rides an engine's streams for as long as the engine runs
/// and must let go completely when it stops or the node is disabled.
/// </summary>
public interface IEngineObserver
{
    /// <summary>Subscribe to a (re)started engine, replacing any prior subscription.</summary>
    void Attach(WatcherEngine engine);
    /// <summary>Drop the subscription and every reference to the engine.</summary>
    void Detach();
}
