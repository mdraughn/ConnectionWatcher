namespace ConInfrastructure.Tray;

/// <summary>
/// Where the scout keeps the one admin password the user asked it to remember. A saved password is bound
/// to the server it was entered for (<see cref="ScoutServerKey"/>): <see cref="Load"/> returns it only for
/// that server, and null for any other. The tray's implementation protects it with DPAPI.
/// </summary>
public interface IScoutPasswordStore
{
    string? Load(Uri server);
    void Save(Uri server, string password);
    void Forget();
}

/// <summary>
/// The rule that binds a saved scout password to its server: the server URL as it was entered, without a
/// trailing slash, compared ignoring case. Every <see cref="IScoutPasswordStore"/> uses this, so a
/// password is never offered to a different URL.
/// </summary>
public static class ScoutServerKey
{
    /// <summary>The key a password saved for <paramref name="server"/> is stored under.</summary>
    public static string Of(Uri server) => Normalize(server.OriginalString);

    /// <summary>Whether a password saved under <paramref name="savedKey"/> may be offered to <paramref name="server"/>.</summary>
    public static bool Matches(string savedKey, Uri server) =>
        string.Equals(Normalize(savedKey), Of(server), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string url) => url.TrimEnd('/');
}
