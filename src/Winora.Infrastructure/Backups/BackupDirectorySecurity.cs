using System.Security.AccessControl;
using System.Security.Principal;

namespace Winora.Infrastructure.Backups;

internal static class BackupDirectorySecurity
{
    private const InheritanceFlags RequiredInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    internal static void CreateUserOnlyDirectoryNew(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var security = CreateUserOnlySecurity();
        SecureBackupDirectoryLayout.CreateDirectoryNew(
            path,
            security.GetSecurityDescriptorBinaryForm());
        VerifyUserOnlyDirectory(path);
    }

    private static DirectorySecurity CreateUserOnlySecurity()
    {

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User ??
            throw new InvalidOperationException("The current Windows identity does not have a user SID.");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            userSid,
            FileSystemRights.FullControl,
            RequiredInheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            RequiredInheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    internal static void VerifyUserOnlyDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User ??
            throw new InvalidOperationException("The current Windows identity does not have a user SID.");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        if (!security.AreAccessRulesProtected)
        {
            throw new InvalidDataException(
                "A backup directory must disable inherited access rules.");
        }

        var userFullControl = false;
        var systemFullControl = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.InheritanceFlags != RequiredInheritance ||
                rule.PropagationFlags != PropagationFlags.None)
            {
                throw new InvalidDataException(
                    "A backup directory access-control rule has unsafe inheritance flags.");
            }

            if (rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl ||
                (!sid.Equals(userSid) && !sid.Equals(systemSid)))
            {
                throw new InvalidDataException(
                    "A backup directory has an unexpected access-control rule.");
            }

            userFullControl |= sid.Equals(userSid);
            systemFullControl |= sid.Equals(systemSid);
        }

        if (!userFullControl || !systemFullControl)
        {
            throw new InvalidDataException(
                "A backup directory must grant full control only to the current user and SYSTEM.");
        }
    }
}
