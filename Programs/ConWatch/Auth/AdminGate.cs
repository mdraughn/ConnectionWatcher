using System.Security.Claims;
using System.Security.Cryptography;

namespace ConWatch.Auth;

/// <summary>
/// The single site password, stored as a salted PBKDF2 hash in a
/// hand-deletable file beside the database. No file means no protection;
/// deleting the file is the documented forgot-password path and takes
/// effect on the next request. A corrupt file counts as enabled but never
/// verifies — the gate must not fail open.
/// </summary>
public class AdminGate(string filePath, ILogger<AdminGate> log)
{
    private const int Iterations = 100_000;

    public string FilePath { get; } = filePath;

    public bool IsEnabled => File.Exists(FilePath);

    public bool IsLocked(ClaimsPrincipal user) =>
        IsEnabled && user.Identity?.IsAuthenticated != true;

    public void Set(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        File.WriteAllText(FilePath,
            $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public void Remove()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    public bool Verify(string password)
    {
        try
        {
            var parts = File.ReadAllText(FilePath).Trim().Split('$');
            if (parts is not ["pbkdf2", _, _, _] || !int.TryParse(parts[1], out var iterations))
            {
                log.LogWarning("Admin password file {Path} is corrupt; delete it to reset", FilePath);
                return false;
            }
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations,
                HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            log.LogWarning(ex, "Admin password file {Path} unreadable; delete it to reset", FilePath);
            return false;
        }
    }
}
