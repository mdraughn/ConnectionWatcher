using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ConWatchCli.Services;

namespace ConWatchCli.Windows;

/// <summary>
/// Protected ACL (nothing inherited from ProgramData): SYSTEM and Administrators
/// full control, Users read-only when asked. Inheritable, so files already in the
/// folder and everything the service creates later get the same. Ownership moves
/// to Administrators so a user who created the folder cannot re-grant themselves.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFolderLockdown : IFolderLockdown
{
    private const InheritanceFlags Everything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public void Lock(string directory, bool usersMayRead)
    {
        var folder = Directory.CreateDirectory(directory);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl));
        acl.AddAccessRule(Allow(admins, FileSystemRights.FullControl));
        if (usersMayRead)
            acl.AddAccessRule(Allow(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute));
        folder.SetAccessControl(acl);

        try
        {
            var owner = new DirectorySecurity();
            owner.SetOwner(admins);
            folder.SetAccessControl(owner);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            // Only possible without elevation (tests); `service install` always runs elevated.
        }
    }

    private static FileSystemAccessRule Allow(SecurityIdentifier who, FileSystemRights rights) =>
        new(who, rights, Everything, PropagationFlags.None, AccessControlType.Allow);
}
