using System.Security.AccessControl;
using System.Security.Principal;
using ConWatchCli.Windows;

namespace ConWatchCli.Tests;

/// <summary>Real ACLs on a temp folder; Windows only (returns early elsewhere).</summary>
public sealed class WindowsFolderLockdownTests
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    [Fact]
    public void Private_lock_leaves_only_system_and_administrators_and_reaches_files_already_there()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("lockdown").FullName;
        var file = Path.Combine(dir, "conwatch.db");
        File.WriteAllText(file, "x");
        try
        {
            new WindowsFolderLockdown().Lock(dir, usersMayRead: false);

            var folder = new DirectoryInfo(dir).GetAccessControl();
            Assert.True(folder.AreAccessRulesProtected); // ProgramData's grants no longer flow in
            Assert.Equal(new[] { Admins, System }.OrderBy(s => s.Value), Grantees(folder).OrderBy(s => s.Value));
            Assert.All(Rules(folder), r => Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights));
            Assert.Equal(new[] { Admins, System }.OrderBy(s => s.Value), Grantees(new FileInfo(file).GetAccessControl()).OrderBy(s => s.Value));
        }
        finally
        {
            Unlock(dir);
        }
    }

    [Fact]
    public void Readable_lock_lets_users_read_but_not_write()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("lockdown").FullName;
        try
        {
            new WindowsFolderLockdown().Lock(dir, usersMayRead: true);

            var users = Rules(new DirectoryInfo(dir).GetAccessControl()).Single(r => r.IdentityReference.Equals(Users));
            Assert.Equal(AccessControlType.Allow, users.AccessControlType);
            Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, users.FileSystemRights);
        }
        finally
        {
            Unlock(dir);
        }
    }

    [Fact]
    public void Lock_creates_a_missing_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "lockdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            new WindowsFolderLockdown().Lock(dir, usersMayRead: false);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Unlock(dir);
        }
    }

    private static List<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    private static IEnumerable<SecurityIdentifier> Grantees(FileSystemSecurity security) =>
        Rules(security).Select(r => (SecurityIdentifier)r.IdentityReference).Distinct();

    /// <summary>A non-elevated test run is locked out too; as owner it can still hand itself access back to clean up.</summary>
    private static void Unlock(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(security);
        Directory.Delete(dir, recursive: true);
    }
}
