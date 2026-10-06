using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConConfig;

namespace ConWatchTray;

/// <summary>
/// The admin password the scout uses, protected with DPAPI for the current Windows user and stored
/// next to tray.json. It is bound to the server URL it was entered for: a different URL never reuses it.
/// A file that cannot be read or decrypted is treated as "no saved password".
/// </summary>
internal sealed class ScoutPasswordStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ConnectionWatcher.Scout.v1");

    private sealed record Stored(string Server, string Protected);

    private readonly string _path;

    public ScoutPasswordStore(ConfigPaths? paths = null)
    {
        var dir = Path.GetDirectoryName((paths ?? ConfigPaths.Default()).UserFile)!;
        _path = Path.Combine(dir, "scout.json");
    }

    public string? Load(string serverUrl)
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            if (stored is null || !string.Equals(Normalize(stored.Server), Normalize(serverUrl), StringComparison.OrdinalIgnoreCase))
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

    public void Save(string serverUrl, string password)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy,
                DataProtectionScope.CurrentUser);
            var json = JsonSerializer.Serialize(new Stored(Normalize(serverUrl), Convert.ToBase64String(protectedBytes)));
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

    private static string Normalize(string url) => url.TrimEnd('/');
}
