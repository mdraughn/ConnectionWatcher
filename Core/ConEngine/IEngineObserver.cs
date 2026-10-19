namespace ConEngine;

/// <summary>
/// Something that rides an engine's streams for as long as the engine runs
/// and must let go completely when it stops or the node is disabled.
/// </summary>
public interface IEngineObserver
{
    /// <summary>Subscribe to a (re)started engine, replacing any prior subscription.</summary>
    void Attach(WatcherEngine engine);
    /// <summary>
    /// The attached engine's StartAsync has returned, so its monitors are all in place. Attach comes
    /// first (no transition is missed), but anything that reads the monitors must wait for this.
    /// </summary>
    void Started(WatcherEngine engine) { }
    /// <summary>Drop the subscription and every reference to the engine.</summary>
    void Detach();
}
