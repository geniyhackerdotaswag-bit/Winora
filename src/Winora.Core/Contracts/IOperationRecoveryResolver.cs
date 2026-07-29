using Winora.Core.Changes;

namespace Winora.Core.Contracts;

public sealed record DurableBackupBinding
{
    private DurableBackupBinding(string backupId, string backupDigest)
    {
        BackupId = backupId;
        BackupDigest = backupDigest;
    }

    public string BackupId { get; }

    public string BackupDigest { get; }

    internal static DurableBackupBinding Create(string backupId, string backupDigest)
    {
        if (!ChangePlan.IsSafeOpaqueStorageId(backupId))
        {
            throw new ArgumentException(
                "A recovery backup identifier must be opaque and path-independent.",
                nameof(backupId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(backupDigest);
        return new DurableBackupBinding(backupId, backupDigest);
    }
}

/// <summary>
/// Sanitized, immutable input for resolving an allowlisted recovery adapter
/// after a restart. Exact paths and backed-up values remain in the verified
/// backup store and are addressed only by opaque recovery keys.
/// </summary>
public sealed record DurableOperationRecoveryRequest
{
    private DurableOperationRecoveryRequest(
        Guid operationId,
        long revision,
        OperationState state,
        string? stepId,
        string catalogOperationId,
        string planDigest,
        StateFingerprint sourceFingerprint,
        PrivilegeRequirement privilege,
        RiskLevel risk,
        RollbackCapability rollback,
        BackupRequirement backup,
        bool requiresRestorePoint,
        ChangePlanKind kind,
        IReadOnlyList<DurableStepRecoveryDescriptor> steps,
        IReadOnlyList<string> appliedStepIds,
        DurableStepRecoveryDescriptor? activeStep,
        DurableBackupBinding? backupBinding,
        DurableBackupBinding? recoveryCheckpointBinding,
        RestorePointTransitionFacts? restorePoint)
    {
        OperationId = operationId;
        Revision = revision;
        State = state;
        StepId = stepId;
        CatalogOperationId = catalogOperationId;
        PlanDigest = planDigest;
        SourceFingerprint = sourceFingerprint;
        Privilege = privilege;
        Risk = risk;
        Rollback = rollback;
        Backup = backup;
        RequiresRestorePoint = requiresRestorePoint;
        Kind = kind;
        Steps = Array.AsReadOnly(steps.ToArray());
        AppliedStepIds = Array.AsReadOnly(appliedStepIds.ToArray());
        ActiveStep = activeStep;
        BackupBinding = backupBinding;
        RecoveryCheckpointBinding = recoveryCheckpointBinding;
        RestorePoint = restorePoint;
    }

    public Guid OperationId { get; }

    public long Revision { get; }

    public OperationState State { get; }

    public string? StepId { get; }

    public string CatalogOperationId { get; }

    public string PlanDigest { get; }

    public StateFingerprint SourceFingerprint { get; }

    public PrivilegeRequirement Privilege { get; }

    public RiskLevel Risk { get; }

    public RollbackCapability Rollback { get; }

    public BackupRequirement Backup { get; }

    public bool RequiresRestorePoint { get; }

    public ChangePlanKind Kind { get; }

    public IReadOnlyList<DurableStepRecoveryDescriptor> Steps { get; }

    public IReadOnlyList<string> AppliedStepIds { get; }

    public DurableStepRecoveryDescriptor? ActiveStep { get; }

    public DurableBackupBinding? BackupBinding { get; }

    public DurableBackupBinding? RecoveryCheckpointBinding { get; }

    public RestorePointTransitionFacts? RestorePoint { get; }

    public static DurableOperationRecoveryRequest FromBoundary(
        DurableOperationBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var facts = boundary.Facts;
        var activeStep = boundary.StepId is null
            ? null
            : facts.RecoverySteps.SingleOrDefault(descriptor =>
                StringComparer.Ordinal.Equals(descriptor.StepId, boundary.StepId));
        if (boundary.StepId is not null && activeStep is null)
        {
            throw new ArgumentException(
                "The durable boundary step has no immutable recovery descriptor.",
                nameof(boundary));
        }

        var descriptorStepIds = facts.RecoverySteps
            .Select(descriptor => descriptor.StepId)
            .ToHashSet(StringComparer.Ordinal);
        if (boundary.AppliedStepIds.Any(stepId => !descriptorStepIds.Contains(stepId)) ||
            boundary.AppliedStepIds.Distinct(StringComparer.Ordinal).Count() !=
            boundary.AppliedStepIds.Count)
        {
            throw new ArgumentException(
                "The verified applied prefix contains no matching recovery descriptor.",
                nameof(boundary));
        }

        var backupBinding = facts.BackupId is null
            ? null
            : DurableBackupBinding.Create(facts.BackupId, facts.BackupDigest!);
        var checkpointBinding = facts.RecoveryCheckpointId is null
            ? null
            : DurableBackupBinding.Create(
                facts.RecoveryCheckpointId,
                facts.RecoveryCheckpointDigest!);
        return new DurableOperationRecoveryRequest(
            boundary.OperationId,
            boundary.Revision,
            boundary.State,
            boundary.StepId,
            facts.CatalogOperationId,
            facts.PlanDigest,
            facts.SourceFingerprint,
            facts.Privilege,
            facts.Risk,
            facts.Rollback,
            facts.Backup,
            facts.RequiresRestorePoint,
            facts.Kind,
            facts.RecoverySteps,
            boundary.AppliedStepIds,
            activeStep,
            backupBinding,
            checkpointBinding,
            boundary.RestorePoint);
    }
}

/// <summary>
/// Resolves only fixed catalog operation IDs. Implementations must reject
/// unknown IDs and must not dynamically load code or execute commands.
/// </summary>
public interface IOperationRecoveryResolver
{
    ValueTask<IResolvedOperationRecovery?> ResolveAsync(
        DurableOperationRecoveryRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A request-bound recovery adapter. The implementation resolves each opaque
/// RecoveryKey through the verified BackupBinding captured at resolution time.
/// It never accepts a raw target path or an arbitrary command.
/// </summary>
public interface IResolvedOperationRecovery
{
    string CatalogOperationId { get; }

    ValueTask<OperationCapability> ProbeAsync(
        DurableStepRecoveryDescriptor step,
        CancellationToken cancellationToken);

    ValueTask<VerificationResult> VerifyAsync(
        DurableStepRecoveryDescriptor step,
        CancellationToken cancellationToken);

    ValueTask<StepResult> RollbackAsync(
        DurableStepRecoveryDescriptor step,
        CancellationToken cancellationToken);
}
