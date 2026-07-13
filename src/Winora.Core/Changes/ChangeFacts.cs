namespace Winora.Core.Changes;

public enum RiskLevel
{
    Informational,
    Low,
    Medium,
    High,
}

public enum PrivilegeRequirement
{
    StandardUser,
    Administrator,
}

public enum RollbackCapability
{
    Full,
    Partial,
    NotAvailable,
    NotApplicable,
}

public enum RestartRequirement
{
    None,
    Explorer,
    SignOut,
    Reboot,
}

public enum SupportStatus
{
    Supported,
    SupportedWithElevation,
    Guided,
    Unsupported,
    Unknown,
    UnsupportedForSafeMutation,
}

public enum BackupRequirement
{
    Required,
    NotApplicable,
}

public enum ChangePlanKind
{
    TargetMutation,
    ManualRestorePointArtifact,
}

public sealed record StateFingerprint(string Algorithm, string Value);

public sealed record DisplayValue(string Kind, string Text);

public sealed record VerificationProbe(string ProbeId, string ExpectedResult);

public sealed record OperationTarget(string TargetId);

public sealed record OperationCapability(
    SupportStatus Support,
    PrivilegeRequirement RequiredPrivilege,
    StateFingerprint CurrentFingerprint,
    bool IsApiAvailable,
    bool IsWritable,
    bool IsBackupAvailable,
    bool IsVerificationAvailable,
    bool IsRollbackAvailable,
    bool IsConditionalMutationAvailable,
    string? BlockReason);

public sealed record ChangeSafetyDecision(bool IsAllowed, string? BlockReason);

public static class ChangeSafetyPolicy
{
    public static ChangeSafetyDecision Evaluate(ChangePlan plan, OperationCapability capability)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(capability);

        if (!plan.HasValidDigest)
        {
            return Block("The plan digest is invalid.");
        }

        if (!IsDirectlySupported(plan.Support) || !IsDirectlySupported(capability.Support))
        {
            return Block(capability.BlockReason ?? "The operation is not supported for direct apply.");
        }

        if (capability.RequiredPrivilege != plan.Privilege)
        {
            return Block("The runtime privilege requirement no longer matches the confirmed plan.");
        }

        if (!capability.IsApiAvailable || !capability.IsWritable || !capability.IsVerificationAvailable)
        {
            return Block(capability.BlockReason ?? "A required runtime capability is unavailable.");
        }

        var isSafetyArtifact =
            plan.Kind == ChangePlanKind.ManualRestorePointArtifact &&
            plan.Risk == RiskLevel.Informational &&
            plan.Backup == BackupRequirement.NotApplicable &&
            plan.Rollback == RollbackCapability.NotApplicable;

        if (!isSafetyArtifact &&
            (plan.Backup != BackupRequirement.Required ||
             plan.Rollback != RollbackCapability.Full ||
             !capability.IsBackupAvailable ||
             !capability.IsRollbackAvailable ||
             !capability.IsConditionalMutationAvailable))
        {
            return Block("Backup, full rollback, and conditional mutation are required.");
        }

        if (plan.Risk == RiskLevel.High && !plan.RequiresRestorePoint)
        {
            return Block("High-risk operations require a System Restore point.");
        }

        return new ChangeSafetyDecision(true, null);
    }

    private static bool IsDirectlySupported(SupportStatus status) =>
        status is SupportStatus.Supported or SupportStatus.SupportedWithElevation;

    private static ChangeSafetyDecision Block(string reason) => new(false, reason);
}

public sealed class ConfirmationToken
{
    private ConfirmationToken(Guid tokenId, string planDigest)
    {
        TokenId = tokenId;
        PlanDigest = planDigest;
    }

    public Guid TokenId { get; }

    public string PlanDigest { get; }

    public static ConfirmationToken Create(ChangePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ConfirmationToken(Guid.NewGuid(), plan.Digest);
    }

    public bool Authorizes(ChangePlan plan) =>
        plan is not null &&
        plan.HasValidDigest &&
        StringComparer.Ordinal.Equals(PlanDigest, plan.Digest);
}
