using Xunit.Sdk;

namespace ConWatch.Tests;

/// <summary>
/// Locates and checks the golden files under Tests/contracts at the repo root.
/// A mismatch fails and prints the new content; the file is rewritten only when
/// UPDATE_CONTRACTS=1 is set, so a contract never changes by accident.
/// </summary>
internal static class ContractFiles
{
    /// <summary>Full path of <c>Tests/contracts/{name}</c>, found by walking up from the test binary to the solution root.</summary>
    public static string PathOf(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ConnectionWatcher.sln")))
                return Path.Combine(dir.FullName, "Tests", "contracts", name);
        }
        throw new InvalidOperationException($"ConnectionWatcher.sln not found above {AppContext.BaseDirectory}.");
    }

    /// <summary>Compares <paramref name="actual"/> to the golden file (line endings ignored).</summary>
    public static void AssertMatches(string name, string actual)
    {
        var path = PathOf(name);
        actual = Normalize(actual);
        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }
        var expected = File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
        if (expected != actual)
            throw new XunitException(
                $"Contract {name} differs. Review the change; if it is intended, rerun with UPDATE_CONTRACTS=1.\n" +
                $"--- expected ({path})\n{expected ?? "(missing)"}\n--- actual\n{actual}");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd() + "\n";
}
