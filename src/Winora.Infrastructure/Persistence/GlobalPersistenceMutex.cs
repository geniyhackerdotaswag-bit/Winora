using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Winora.Infrastructure.Persistence;

internal sealed class GlobalPersistenceMutex
{
    private static readonly Lazy<GlobalPersistenceMutex> SharedInstance =
        new(() => new GlobalPersistenceMutex(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Mutex _mutex;

    private GlobalPersistenceMutex()
    {
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

        var sidHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(userSid.Value)));
        var mutexName = $"Global\\Winora.Persistence.{sidHash}";

        // Microsoft Learn: https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutexacl.create?view=net-10.0
        _mutex = MutexAcl.Create(
            initiallyOwned: false,
            mutexName,
            out _,
            security);
    }

    internal static GlobalPersistenceMutex Shared => SharedInstance.Value;

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
}
