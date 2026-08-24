using System.Runtime.InteropServices;
using Winora.Core.Contracts;

namespace Winora.App.Services;

/// <summary>
/// Whether this process may apply changes at all.
/// </summary>
/// <remarks>
/// It used to mean "is this the packaged build", because the mutation lease would only be granted
/// to a registered package. That rule was written when unpackaged meant a developer's launch from
/// the debugger. It now also means the portable .exe — the only build a person outside this project
/// can get — and a program handed to somebody that refuses every change it offers is not a program.
/// The lease itself was taught an unpackaged identity on 2026-08-24; see
/// <c>WindowsMutationLeaseOwnerIdentity</c> for what that gives up and what it keeps.
/// </remarks>
public interface IDeploymentState
{
    bool IsPackaged { get; }

    bool CanApplyChanges { get; }

    /// <summary>Resource key explaining why applying is unavailable, or null when it is available.</summary>
    string? ApplyBlockReasonKey { get; }
}

/// <inheritdoc />
public sealed class DeploymentState : IDeploymentState
{
    private const int AppModelErrorNoPackage = 15700;

    public DeploymentState()
    {
        IsPackaged = DetectPackageIdentity();
    }

    public bool IsPackaged { get; }

    /// <remarks>
    /// Always. Both shapes of the app can hold the lease now, and the machinery that actually
    /// guards a change — the plan, the backup, the lease, the verification — is the same in each.
    /// Kept as a property rather than deleted because the screens ask this question and there may
    /// yet be a state that answers no.
    /// </remarks>
    public bool CanApplyChanges => true;

    public string? ApplyBlockReasonKey => null;

    /// <remarks>
    /// Microsoft Learn: https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getcurrentpackagefullname
    /// </remarks>
    private static bool DetectPackageIdentity()
    {
        // Only the return code matters, so no buffer is passed: APPMODEL_ERROR_NO_PACKAGE means this
        // process has no package identity, anything else means it does.
        var length = 0u;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}

/// <summary>
/// Stands in for the mutation lease when this process has no package identity. It never grants a
/// lease, so no code path can mutate the system while unable to prove who holds the lock.
/// </summary>
public sealed class UnavailableMutationLease : IMutationLease
{
    public ValueTask<IMutationLeaseHandle?> TryAcquireAsync(Guid operationId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IMutationLeaseHandle?>(null);

    public ValueTask<IMutationLeaseHandle?> TryAcquireRecoveryAsync(
        Guid incompleteOperationId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IMutationLeaseHandle?>(null);
}
