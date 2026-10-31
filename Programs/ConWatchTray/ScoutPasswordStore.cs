using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConConfig;
using ConInfrastructure.Tray;

namespace ConWatchTray;

/// <summary>
/// The admin password the scout uses, protected with DPAPI for the current Windows user and stored
/// next to tray.json. It is bound to the server URL it was entered for (<see cref="ScoutServerKey"/>):
/// a different URL never reuses it. A file that cannot be read or decrypted is treated as "no saved password".
/// </summary>
internal sealed class ScoutPasswordStore : IScoutPasswordStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ConnectionWatcher.Scout.v1");

    private sealed record Stored(string Server, string Protected);

    private readonly string _path;

    public ScoutPasswordStore()
    {
        var dir = Path.GetDirectoryName(ConfigPaths.Default().UserFile)!;
        _path = Path.Combine(dir, "scout.json");
    }

    public string? Load(Uri server)
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            if (stored is null || !ScoutServerKey.Matches(stored.Server, server))
                return null;
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.Protected), Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(Uri server, string password)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy,
                DataProtectionScope.CurrentUser);
            var json = JsonSerializer.Serialize(new Stored(ScoutServerKey.Of(server), Convert.ToBase64String(protectedBytes)));
            File.WriteAllText(_path, json);
        }
        catch (Exception)
        {
            // A password that cannot be saved is simply asked for again next time.
        }
    }

    public bool HasSaved => File.Exists(_path);

    public void Forget()
    {
        try { File.Delete(_path); } catch (Exception) { }
    }
}
