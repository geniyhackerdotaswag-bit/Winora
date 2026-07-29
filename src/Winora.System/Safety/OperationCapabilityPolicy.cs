using Winora.Core.Changes;

namespace Winora.System.Safety;

/// <summary>
/// Turns one adapter's raw <see cref="CapabilityObservation"/> into the
/// <see cref="OperationCapability"/> the Core coordinator consumes.
/// </summary>
/// <remarks>
/// <para>
/// The order below is deliberate and is asserted by tests. Facts that make the target itself
/// unusable are reported before facts about Winora's own safety machinery, so a user is told
/// "this target cannot be changed" rather than a misleading "backup unavailable".
/// </para>
/// <para>
/// Nothing here ever produces a permissive result from an incomplete observation: a support
/// status of <see cref="SupportStatus.Supported"/> or
/// <see cref="SupportStatus.SupportedWithElevation"/> requires every safety fact to hold, which
/// is what <see cref="ChangeSafetyPolicy"/> independently re-checks before apply.
/// </para>
/// </remarks>
public static class OperationCapabilityPolicy
{
    public static OperationCapability Evaluate(CapabilityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var (support, blockReason) = Decide(observation);

        return new OperationCapability(
            support,
            observation.RequiredPrivilege,
            observation.CurrentFingerprint,
            observation.IsApiAvailable,
            observation.IsWritable,
            observation.IsBackupAvailable,
            observation.IsVerificationAvailable,
            observation.IsRollbackAvailable,
            observation.IsConditionalMutationAvailable,
            blockReason);
    }

    private static (SupportStatus Support, string? BlockReason) Decide(CapabilityObservation observation)
    {
        // 1. The documented mechanism must exist at all.
        if (!observation.IsApiAvailable)
        {
            return (SupportStatus.Unsupported, CapabilityBlockCodes.ApiNotAvailable);
        }

        // 2. An unreadable source state is Unknown, never Unsupported: Winora does not claim to
        //    know that a change is impossible when it could not observe the target.
        if (!observation.IsTargetStateKnown)
        {
            return (SupportStatus.Unknown, CapabilityBlockCodes.TargetStateUnknown);
        }

        // 3. Target shape.
        if (observation.IsRemoteTarget)
        {
            return (SupportStatus.Unsupported, CapabilityBlockCodes.TargetRemote);
        }

        if (observation.IsProtectedTarget)
        {
            return (SupportStatus.Unsupported, CapabilityBlockCodes.TargetProtected);
        }

        if (!observation.IsWritable)
        {
            return (SupportStatus.Unsupported, CapabilityBlockCodes.TargetNotWritable);
        }

        // 4. Rights. Reported before any UAC prompt is prepared.
        if (observation.RequiredPrivilege == PrivilegeRequirement.Administrator &&
            !observation.IsElevationSupportedForAccount)
        {
            return (SupportStatus.Unsupported, CapabilityBlockCodes.ElevationUnsupportedForCurrentAccount);
        }

        // 5. Winora's own safety guarantees. Missing conditional mutation or independent
        //    verification is a safety limit rather than a Windows limit, so it degrades to
        //    UnsupportedForSafeMutation instead of pretending the target is unsupported.
        if (!observation.IsConditionalMutationAvailable)
        {
            return (SupportStatus.UnsupportedForSafeMutation, CapabilityBlockCodes.ConditionalMutationUnavailable);
        }

        if (!observation.IsVerificationAvailable)
        {
            return (SupportStatus.UnsupportedForSafeMutation, CapabilityBlockCodes.VerificationUnavailable);
        }

        if (!observation.IsBackupAvailable)
        {
            return (SupportStatus.UnsupportedForSafeMutation, CapabilityBlockCodes.BackupUnavailable);
        }

        if (!observation.IsRollbackAvailable)
        {
            return (SupportStatus.Partial, CapabilityBlockCodes.RollbackNotFull);
        }

        return observation.RequiredPrivilege == PrivilegeRequirement.Administrator
            ? (SupportStatus.SupportedWithElevation, null)
            : (SupportStatus.Supported, null);
    }
}
