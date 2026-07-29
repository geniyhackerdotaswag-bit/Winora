using System.Security.AccessControl;
using System.Security.Principal;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Tests.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Backups;

public sealed class BackupDirectorySecurityTests
{
    private const InheritanceFlags RequiredInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    [Theory]
    [InlineData(InheritanceFlags.None, PropagationFlags.None)]
    [InlineData(InheritanceFlags.ContainerInherit, PropagationFlags.None)]
    [InlineData(InheritanceFlags.ObjectInherit, PropagationFlags.None)]
    [InlineData(RequiredInheritance, PropagationFlags.InheritOnly)]
    [InlineData(RequiredInheritance, PropagationFlags.NoPropagateInherit)]
    public void Verification_rejects_rules_that_do_not_apply_full_control_to_directory_and_all_children(
        InheritanceFlags inheritance,
        PropagationFlags propagation)
    {
        using var directory = new TemporaryDirectory();
        SetUserAndSystemAcl(directory.Path, inheritance, propagation);

        try
        {
            var failure = Assert.Throws<InvalidDataException>(() =>
                BackupDirectorySecurity.VerifyUserOnlyDirectory(directory.Path));

            Assert.Contains("inheritance", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SetUserAndSystemAcl(
                directory.Path,
                RequiredInheritance,
                PropagationFlags.None);
        }
    }

    [Fact]
    public void Created_directory_has_a_protected_dacl_inherited_by_files_and_subdirectories()
    {
        using var root = new TemporaryDirectory();
        var path = Path.Combine(root.Path, "backup");

        BackupDirectorySecurity.CreateUserOnlyDirectoryNew(path);

        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(RequiredInheritance, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        });
    }

    private static void SetUserAndSystemAcl(
        string path,
        InheritanceFlags inheritance,
        PropagationFlags propagation)
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
            inheritance,
            propagation,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            inheritance,
            propagation,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
