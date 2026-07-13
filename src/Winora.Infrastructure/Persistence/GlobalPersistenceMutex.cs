using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Winora.Infrastructure.Persistence;

internal sealed class GlobalPersistenceMutex : IDisposable
{
    private static readonly Lazy<GlobalPersistenceMutex> SharedInstance =
        new(() => new GlobalPersistenceMutex(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Mutex _mutex;

    private GlobalPersistenceMutex()
        : this(GetCurrentUserMutexName())
    {
    }

    internal GlobalPersistenceMutex(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User ??
            throw new InvalidOperationException("The current Windows identity does not have a user SID.");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(
            userSid,
            MutexRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(
            systemSid,
            MutexRights.FullControl,
            AccessControlType.Allow));

        // Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutexacl.create?view=net-10.0
        _mutex = MutexAcl.Create(
            initiallyOwned: false,
            mutexName,
            out var createdNew,
            security);
        try
        {
            ValidateExactAcl(_mutex, userSid, systemSid, createdNew);
        }
        catch
        {
            _mutex.Dispose();
            throw;
        }
    }

    internal static GlobalPersistenceMutex Shared => SharedInstance.Value;

    public void Dispose() => _mutex.Dispose();

    internal T Execute<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        var acquired = false;
        try
        {
            try
            {
                var result = WaitHandle.WaitAny([_mutex, cancellationToken.WaitHandle]);
                if (result != 0)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                acquired = true;
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }
        finally
        {
            if (acquired)
            {
                _mutex.ReleaseMutex();
            }
        }
    }

    private static string GetCurrentUserMutexName()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User ??
            throw new InvalidOperationException("The current Windows identity does not have a user SID.");
        var sidHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(userSid.Value)));
        return $"Global\\Winora.Persistence.{sidHash}";
    }

    private static void ValidateExactAcl(
        Mutex mutex,
        SecurityIdentifier userSid,
        SecurityIdentifier systemSid,
        bool createdNew)
    {
        var actual = mutex.GetAccessControl();
        var rules = actual.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<MutexAccessRule>()
            .ToArray();
        var expectedSids = new HashSet<SecurityIdentifier> { userSid, systemSid };
        var exact = actual.AreAccessRulesProtected &&
            rules.Length == expectedSids.Count &&
            rules.All(rule =>
                !rule.IsInherited &&
                rule.AccessControlType == AccessControlType.Allow &&
                rule.MutexRights == MutexRights.FullControl &&
                expectedSids.Remove((SecurityIdentifier)rule.IdentityReference)) &&
            expectedSids.Count == 0;
        if (!exact)
        {
            var origin = createdNew ? "newly created" : "pre-existing";
            throw new InvalidOperationException(
                $"The {origin} Winora persistence mutex does not have the required protected user/SYSTEM ACL.");
        }
    }
}
