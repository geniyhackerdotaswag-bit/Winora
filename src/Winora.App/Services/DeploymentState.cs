using System.Runtime.InteropServices;
using Winora.Core.Contracts;

namespace Winora.App.Services;

/// <summary>
/// Whether this process may apply changes at all. The mutation lease proves its owner is the signed
/// Winora package, so an unpackaged development launch can browse, probe, preview, and review, but
/// cannot hold the lease and therefore cannot apply.
/// </summary>
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

    public bool CanApplyChanges => IsPackaged;

    public string? ApplyBlockReasonKey => IsPackaged ? null : "Deployment_UnpackagedCannotApply";

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
