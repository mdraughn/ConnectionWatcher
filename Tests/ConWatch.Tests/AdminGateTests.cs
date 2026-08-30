using ConWatch.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConWatch.Tests;

public class AdminGateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("conwatch-gate").FullName;
    private readonly AdminGate _gate;

    public AdminGateTests() =>
        _gate = new AdminGate(Path.Combine(_dir, "admin-password.txt"), NullLogger<AdminGate>.Instance);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Disabled_until_set_then_verifies_round_trip()
    {
        Assert.False(_gate.IsEnabled);
        _gate.Set("hunter2");
        Assert.True(_gate.IsEnabled);
        Assert.True(_gate.Verify("hunter2"));
        Assert.False(_gate.Verify("wrong"));
        Assert.False(_gate.Verify(""));
    }

    [Fact]
    public void File_is_a_hash_not_the_password()
    {
        _gate.Set("hunter2");
        var content = File.ReadAllText(_gate.FilePath);
        Assert.StartsWith("pbkdf2$100000$", content);
        Assert.DoesNotContain("hunter2", content);
    }

    [Fact]
    public void Remove_and_manual_delete_both_disable()
    {
        _gate.Set("hunter2");
        _gate.Remove();
        Assert.False(_gate.IsEnabled);
        _gate.Set("again");
        File.Delete(_gate.FilePath); // the documented forgot-password path
        Assert.False(_gate.IsEnabled);
    }

    [Fact]
    public void Corrupt_file_is_enabled_but_never_verifies()
    {
        File.WriteAllText(_gate.FilePath, "garbage");
        Assert.True(_gate.IsEnabled);   // never fail open
        Assert.False(_gate.Verify("anything"));
    }

    [Fact]
    public void IsLocked_depends_on_enabled_and_authentication()
    {
        var anon = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        var admin = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity("Cookies"));
        Assert.False(_gate.IsLocked(anon));      // gate off -> open
        _gate.Set("hunter2");
        Assert.True(_gate.IsLocked(anon));
        Assert.False(_gate.IsLocked(admin));
    }
}
