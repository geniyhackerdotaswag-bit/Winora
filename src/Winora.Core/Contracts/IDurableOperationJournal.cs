using System.Security.Cryptography;
using System.Text;
using Winora.Core.Changes;

namespace Winora.Core.Contracts;

public sealed record DurableOperationFacts
{
    private DurableOperationFacts(
        string planDigest,
        StateFingerprint sourceFingerprint,
        IReadOnlyList<string> orderedStepIds,
        PrivilegeRequirement privilege,
        RiskLevel risk,
        RollbackCapability rollback,
        BackupRequirement backup,
        bool requiresRestorePoint,
        ChangePlanKind kind,
        string? backupDigest,
        string? recoveryCheckpointId,
        string? recoveryCheckpointDigest)
    {
        PlanDigest = planDigest;
        SourceFingerprint = sourceFingerprint;
        OrderedStepIds = Array.AsReadOnly(orderedStepIds.ToArray());
        Privilege = privilege;
        Risk = risk;
        Rollback = rollback;
        Backup = backup;
        RequiresRestorePoint = requiresRestorePoint;
        Kind = kind;
        BackupDigest = backupDigest;
        RecoveryCheckpointId = recoveryCheckpointId;
        RecoveryCheckpointDigest = recoveryCheckpointDigest;
        Digest = ComputeDigest(this);
    }

    public string PlanDigest { get; }

    public StateFingerprint SourceFingerprint { get; }

    public IReadOnlyList<string> OrderedStepIds { get; }

    public PrivilegeRequirement Privilege { get; }

    public RiskLevel Risk { get; }

    public RollbackCapability Rollback { get; }

    public BackupRequirement Backup { get; }

    public bool RequiresRestorePoint { get; }

    public ChangePlanKind Kind { get; }

    public string? BackupDigest { get; }

    public string? RecoveryCheckpointId { get; }

    public string? RecoveryCheckpointDigest { get; }

    public string Digest { get; }

    internal static DurableOperationFacts From(ChangePlan plan) =>
        new(
            plan.Digest,
            plan.SourceFingerprint,
            Array.AsReadOnly(plan.Steps.Select(step => step.StepId).ToArray()),
            plan.Privilege,
            plan.Risk,
            plan.Rollback,
            plan.Backup,
            plan.RequiresRestorePoint,
            plan.Kind,
            null,
            null,
            null);

    internal static DurableOperationFacts From(RollbackPlan plan) =>
        new(
            plan.Digest,
            plan.AppliedFingerprint,
            Array.AsReadOnly(plan.Steps.Select(step => step.StepId).ToArray()),
            plan.ChangePlan.Privilege,
            plan.ChangePlan.Risk,
            plan.ChangePlan.Rollback,
            plan.ChangePlan.Backup,
            plan.ChangePlan.RequiresRestorePoint,
            plan.ChangePlan.Kind,
            plan.BackupDigest,
            null,
            null);

    internal DurableOperationFacts WithBackupDigest(string backupDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDigest);
        return new DurableOperationFacts(
            PlanDigest,
            SourceFingerprint,
            OrderedStepIds,
            Privilege,
            Risk,
            Rollback,
            Backup,
            RequiresRestorePoint,
            Kind,
            backupDigest,
            RecoveryCheckpointId,
            RecoveryCheckpointDigest);
    }

    internal DurableOperationFacts WithRecoveryCheckpoint(string checkpointId, string checkpointDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointDigest);
        return new DurableOperationFacts(
            PlanDigest,
            SourceFingerprint,
            OrderedStepIds,
            Privilege,
            Risk,
            Rollback,
            Backup,
            RequiresRestorePoint,
            Kind,
            BackupDigest,
            checkpointId,
            checkpointDigest);
    }

    internal bool Continues(DurableOperationFacts previous, OperationState state)
    {
        var planFactsMatch =
            StringComparer.Ordinal.Equals(PlanDigest, previous.PlanDigest) &&
            SourceFingerprint == previous.SourceFingerprint &&
            OrderedStepIds.SequenceEqual(previous.OrderedStepIds, StringComparer.Ordinal) &&
            Privilege == previous.Privilege &&
            Risk == previous.Risk &&
            Rollback == previous.Rollback &&
            Backup == previous.Backup &&
            RequiresRestorePoint == previous.RequiresRestorePoint &&
            Kind == previous.Kind;
        var backupContinues = previous.BackupDigest is null
            ? BackupDigest is null ||
              (state == OperationState.BackupCreated && !string.IsNullOrWhiteSpace(BackupDigest))
            : StringComparer.Ordinal.Equals(BackupDigest, previous.BackupDigest);
        var checkpointContinues = previous.RecoveryCheckpointId is null &&
                                  previous.RecoveryCheckpointDigest is null
            ? (RecoveryCheckpointId is null && RecoveryCheckpointDigest is null) ||
              (state == OperationState.RollbackCheckpointCreated &&
               !string.IsNullOrWhiteSpace(RecoveryCheckpointId) &&
               !string.IsNullOrWhiteSpace(RecoveryCheckpointDigest))
            : StringComparer.Ordinal.Equals(RecoveryCheckpointId, previous.RecoveryCheckpointId) &&
              StringComparer.Ordinal.Equals(RecoveryCheckpointDigest, previous.RecoveryCheckpointDigest);

        return planFactsMatch && backupContinues && checkpointContinues;
    }

    private static string ComputeDigest(DurableOperationFacts facts)
    {
        var canonical = new StringBuilder();
        Append(canonical, facts.PlanDigest);
        Append(canonical, facts.SourceFingerprint.Algorithm);
        Append(canonical, facts.SourceFingerprint.Value);
        Append(canonical, facts.OrderedStepIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var stepId in facts.OrderedStepIds)
        {
            Append(canonical, stepId);
        }

        Append(canonical, ((int)facts.Privilege).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, ((int)facts.Risk).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, ((int)facts.Rollback).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, ((int)facts.Backup).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, facts.RequiresRestorePoint ? "1" : "0");
        Append(canonical, ((int)facts.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendOptional(canonical, facts.BackupDigest);
        AppendOptional(canonical, facts.RecoveryCheckpointId);
        AppendOptional(canonical, facts.RecoveryCheckpointDigest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendOptional(StringBuilder builder, string? value)
    {
        Append(builder, value is null ? "0" : "1");
        if (value is not null)
        {
            Append(builder, value);
        }
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value));
        builder.Append(':');
        builder.Append(value);
    }
}

public enum RestorePointApiStatus
{
    NotCalled,
    Succeeded,
    KnownFailure,
    OutcomeUnknown,
}

public enum RestorePointOwnershipStatus
{
    NotChecked,
    VerifiedNewAndOwned,
    ReusedOrPreexisting,
    Ambiguous,
}

public enum RestorePointFinalizationMode
{
    None,
    Normal,
    Cancelled,
}

public sealed record RestorePointTransitionFacts
{
    private RestorePointTransitionFacts(
        Guid correlationId,
        string description,
        StateFingerprint preBeginInventoryFingerprint,
        StateFingerprint? postBeginInventoryFingerprint,
        long? sequenceNumber,
        RestorePointApiStatus beginApiStatus,
        RestorePointOwnershipStatus ownershipStatus,
        RestorePointFinalizationMode finalizationMode,
        DateTimeOffset? beginReturnedAtUtc,
        DateTimeOffset? finalizationRequestedAtUtc,
        RestorePointApiStatus finalizationApiStatus)
    {
        CorrelationId = correlationId;
        Description = description;
        PreBeginInventoryFingerprint = preBeginInventoryFingerprint;
        PostBeginInventoryFingerprint = postBeginInventoryFingerprint;
        SequenceNumber = sequenceNumber;
        BeginApiStatus = beginApiStatus;
        OwnershipStatus = ownershipStatus;
        FinalizationMode = finalizationMode;
        BeginReturnedAtUtc = beginReturnedAtUtc;
        FinalizationRequestedAtUtc = finalizationRequestedAtUtc;
        FinalizationApiStatus = finalizationApiStatus;
        Digest = ComputeDigest(this);
    }

    public Guid CorrelationId { get; }

    public string Description { get; }

    public StateFingerprint PreBeginInventoryFingerprint { get; }

    public StateFingerprint? PostBeginInventoryFingerprint { get; }

    public long? SequenceNumber { get; }

    public RestorePointApiStatus BeginApiStatus { get; }

    public RestorePointOwnershipStatus OwnershipStatus { get; }

    public RestorePointFinalizationMode FinalizationMode { get; }

    public DateTimeOffset? BeginReturnedAtUtc { get; }

    public DateTimeOffset? FinalizationRequestedAtUtc { get; }

    public RestorePointApiStatus FinalizationApiStatus { get; }

    public string Digest { get; }

    internal static RestorePointTransitionFacts Create(
        Guid correlationId,
        string description,
        StateFingerprint preBeginInventoryFingerprint,
        StateFingerprint? postBeginInventoryFingerprint,
        long? sequenceNumber,
        RestorePointApiStatus beginApiStatus,
        RestorePointOwnershipStatus ownershipStatus,
        RestorePointFinalizationMode finalizationMode,
        DateTimeOffset? beginReturnedAtUtc,
        DateTimeOffset? finalizationRequestedAtUtc,
        RestorePointApiStatus finalizationApiStatus)
    {
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("A restore-point correlation identifier is required.", nameof(correlationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(preBeginInventoryFingerprint);
        if (sequenceNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber));
        }

        if (beginReturnedAtUtc is { } beginReturned &&
            finalizationRequestedAtUtc is { } finalizationRequested &&
            finalizationRequested < beginReturned)
        {
            throw new ArgumentException("Finalization cannot be requested before begin returns.");
        }

        return new RestorePointTransitionFacts(
            correlationId,
            description,
            preBeginInventoryFingerprint,
            postBeginInventoryFingerprint,
            sequenceNumber,
            beginApiStatus,
            ownershipStatus,
            finalizationMode,
            beginReturnedAtUtc,
            finalizationRequestedAtUtc,
            finalizationApiStatus);
    }

    internal bool IsValidFor(OperationState state)
    {
        var initialBegin =
            SequenceNumber is null &&
            BeginApiStatus == RestorePointApiStatus.NotCalled &&
            OwnershipStatus == RestorePointOwnershipStatus.NotChecked &&
            PostBeginInventoryFingerprint is null &&
            BeginReturnedAtUtc is null;
        var returnedBegin =
            SequenceNumber is not null &&
            BeginApiStatus == RestorePointApiStatus.Succeeded &&
            BeginReturnedAtUtc is not null;
        var verifiedBegin =
            returnedBegin &&
            PostBeginInventoryFingerprint is not null &&
            OwnershipStatus == RestorePointOwnershipStatus.VerifiedNewAndOwned;
        var noFinalization =
            FinalizationMode == RestorePointFinalizationMode.None &&
            FinalizationRequestedAtUtc is null &&
            FinalizationApiStatus == RestorePointApiStatus.NotCalled;
        var finalizationRequested =
            verifiedBegin &&
            FinalizationMode != RestorePointFinalizationMode.None &&
            FinalizationRequestedAtUtc is not null;
        var knownBeginFailureWithNoChanges =
            BeginApiStatus == RestorePointApiStatus.KnownFailure &&
            SequenceNumber is null &&
            PostBeginInventoryFingerprint is null &&
            OwnershipStatus == RestorePointOwnershipStatus.NotChecked &&
            noFinalization;
        var reusedOrPreexistingClosedWithoutCancellation =
            returnedBegin &&
            PostBeginInventoryFingerprint is not null &&
            OwnershipStatus == RestorePointOwnershipStatus.ReusedOrPreexisting &&
            FinalizationMode == RestorePointFinalizationMode.Normal &&
            FinalizationRequestedAtUtc is not null &&
            FinalizationApiStatus == RestorePointApiStatus.Succeeded;

        return state switch
        {
            OperationState.RestorePointBeginRequested => initialBegin && noFinalization,
            OperationState.RestorePointBeginReturnedUnverified =>
                returnedBegin &&
                OwnershipStatus == RestorePointOwnershipStatus.NotChecked &&
                noFinalization,
            OperationState.RestorePointBegun => verifiedBegin && noFinalization,
            OperationState.RestorePointEndRequested =>
                finalizationRequested &&
                FinalizationMode == RestorePointFinalizationMode.Normal &&
                FinalizationApiStatus == RestorePointApiStatus.NotCalled,
            OperationState.RestorePointCancelRequested =>
                finalizationRequested &&
                FinalizationMode == RestorePointFinalizationMode.Cancelled &&
                FinalizationApiStatus == RestorePointApiStatus.NotCalled,
            OperationState.RestorePointEnded =>
                finalizationRequested &&
                FinalizationMode == RestorePointFinalizationMode.Normal &&
                FinalizationApiStatus == RestorePointApiStatus.Succeeded,
            OperationState.RestorePointCancelled =>
                finalizationRequested &&
                FinalizationMode == RestorePointFinalizationMode.Cancelled &&
                FinalizationApiStatus == RestorePointApiStatus.Succeeded,
            OperationState.RestorePointFinalizeFailedRecoveryRequired =>
                finalizationRequested &&
                FinalizationApiStatus == RestorePointApiStatus.KnownFailure,
            OperationState.RestorePointFinalizeOutcomeUnknown =>
                finalizationRequested &&
                FinalizationApiStatus == RestorePointApiStatus.OutcomeUnknown,
            OperationState.RestorePointFailedNoChanges =>
                knownBeginFailureWithNoChanges ||
                reusedOrPreexistingClosedWithoutCancellation,
            OperationState.RestorePointRecoveryRequired =>
                BeginApiStatus == RestorePointApiStatus.OutcomeUnknown ||
                OwnershipStatus == RestorePointOwnershipStatus.Ambiguous ||
                FinalizationApiStatus == RestorePointApiStatus.OutcomeUnknown,
            _ => verifiedBegin,
        };
    }

    internal bool Continues(RestorePointTransitionFacts previous)
    {
        var isReviewedRetry =
            previous.FinalizationApiStatus == RestorePointApiStatus.KnownFailure &&
            FinalizationApiStatus == RestorePointApiStatus.NotCalled;
        return CorrelationId == previous.CorrelationId &&
            StringComparer.Ordinal.Equals(Description, previous.Description) &&
            PreBeginInventoryFingerprint == previous.PreBeginInventoryFingerprint &&
            (previous.PostBeginInventoryFingerprint is null ||
             PostBeginInventoryFingerprint == previous.PostBeginInventoryFingerprint) &&
            (previous.SequenceNumber is null || SequenceNumber == previous.SequenceNumber) &&
            (previous.BeginApiStatus == RestorePointApiStatus.NotCalled ||
             BeginApiStatus == previous.BeginApiStatus) &&
            (previous.BeginReturnedAtUtc is null || BeginReturnedAtUtc == previous.BeginReturnedAtUtc) &&
            (previous.OwnershipStatus == RestorePointOwnershipStatus.NotChecked ||
             OwnershipStatus == previous.OwnershipStatus) &&
            (previous.FinalizationMode == RestorePointFinalizationMode.None ||
             FinalizationMode == previous.FinalizationMode) &&
            (previous.FinalizationRequestedAtUtc is null ||
             FinalizationRequestedAtUtc == previous.FinalizationRequestedAtUtc ||
             isReviewedRetry) &&
            (previous.FinalizationApiStatus == RestorePointApiStatus.NotCalled ||
             FinalizationApiStatus == previous.FinalizationApiStatus ||
             isReviewedRetry);
    }

    private static string ComputeDigest(RestorePointTransitionFacts facts)
    {
        var canonical = new StringBuilder();
        Append(canonical, facts.CorrelationId.ToString("D"));
        Append(canonical, facts.Description);
        Append(canonical, facts.PreBeginInventoryFingerprint.Algorithm);
        Append(canonical, facts.PreBeginInventoryFingerprint.Value);
        AppendOptionalFingerprint(canonical, facts.PostBeginInventoryFingerprint);
        AppendOptional(canonical, facts.SequenceNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, ((int)facts.BeginApiStatus).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, ((int)facts.OwnershipStatus).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, ((int)facts.FinalizationMode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendOptional(canonical, facts.BeginReturnedAtUtc?.ToString("O"));
        AppendOptional(canonical, facts.FinalizationRequestedAtUtc?.ToString("O"));
        Append(canonical, ((int)facts.FinalizationApiStatus).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendOptionalFingerprint(StringBuilder builder, StateFingerprint? fingerprint)
    {
        Append(builder, fingerprint is null ? "0" : "1");
        if (fingerprint is not null)
        {
            Append(builder, fingerprint.Algorithm);
            Append(builder, fingerprint.Value);
        }
    }

    private static void AppendOptional(StringBuilder builder, string? value)
    {
        Append(builder, value is null ? "0" : "1");
        if (value is not null)
        {
            Append(builder, value);
        }
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value));
        builder.Append(':');
        builder.Append(value);
    }
}

public sealed record OperationTransition
{
    private OperationTransition(
        Guid operationId,
        DurableOperationFacts facts,
        long expectedRevision,
        OperationState? expectedState,
        OperationState state,
        string? stepId,
        DateTimeOffset occurredAtUtc,
        RestorePointTransitionFacts? restorePoint,
        string? expectedFactsDigest,
        string? expectedRestorePointFactsDigest)
    {
        OperationId = operationId;
        Facts = facts;
        ExpectedRevision = expectedRevision;
        ExpectedState = expectedState;
        State = state;
        StepId = stepId;
        OccurredAtUtc = occurredAtUtc;
        RestorePoint = restorePoint;
        ExpectedFactsDigest = expectedFactsDigest;
        ExpectedRestorePointFactsDigest = expectedRestorePointFactsDigest;
        TransitionHash = ComputeHash(this);
    }

    public Guid OperationId { get; }

    public DurableOperationFacts Facts { get; }

    public long ExpectedRevision { get; }

    public OperationState? ExpectedState { get; }

    public OperationState State { get; }

    public string? StepId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public RestorePointTransitionFacts? RestorePoint { get; }

    public string? ExpectedFactsDigest { get; }

    public string? ExpectedRestorePointFactsDigest { get; }

    public string TransitionHash { get; }

    internal static OperationTransition Create(
        Guid operationId,
        DurableOperationFacts facts,
        long expectedRevision,
        OperationState? expectedState,
        OperationState state,
        string? stepId,
        DateTimeOffset occurredAtUtc,
        RestorePointTransitionFacts? restorePoint = null,
        RestorePointTransitionFacts? previousRestorePoint = null,
        DurableOperationFacts? previousFacts = null)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A durable operation identifier is required.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(facts);
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        var transitionAllowed = expectedState is { } current
            ? OperationStatePolicy.CanTransition(current, state) ||
              IsReviewedFinalizationRetry(current, state, restorePoint, previousRestorePoint)
            : OperationStatePolicy.CanStartWith(state);
        if (!transitionAllowed)
        {
            throw new ArgumentException("The durable state transition is not allowed.", nameof(state));
        }

        if (expectedState is null)
        {
            if (previousFacts is not null)
            {
                throw new ArgumentException("An initial transition cannot replace prior durable facts.", nameof(previousFacts));
            }
        }
        else if (previousFacts is null || !facts.Continues(previousFacts, state))
        {
            throw new ArgumentException(
                "Durable facts do not continue the exact verified prior facts.",
                nameof(previousFacts));
        }

        if (stepId is not null && !facts.OrderedStepIds.Contains(stepId, StringComparer.Ordinal))
        {
            throw new ArgumentException("The transition step must belong to the durable plan facts.", nameof(stepId));
        }

        if (RequiresExactStep(state) && stepId is null)
        {
            throw new ArgumentException("This durable state requires its exact plan step identifier.", nameof(stepId));
        }

        if (IsRestorePointState(state) && restorePoint is null)
        {
            throw new ArgumentException(
                "Restore lifecycle states require typed restore-point facts.",
                nameof(restorePoint));
        }

        if (restorePoint is null && previousRestorePoint is not null)
        {
            throw new ArgumentException("An active restore lifecycle cannot be dropped from a transition.", nameof(restorePoint));
        }

        if (restorePoint is not null)
        {
            if (IsRestorePointState(state) && !restorePoint.IsValidFor(state))
            {
                throw new ArgumentException("Restore-point facts are inconsistent with the lifecycle state.", nameof(restorePoint));
            }
            else if (!IsRestorePointState(state) &&
                     previousRestorePoint is not null &&
                     !StringComparer.Ordinal.Equals(restorePoint.Digest, previousRestorePoint.Digest))
            {
                throw new ArgumentException(
                    "Restore-point facts may only change on an explicit restore lifecycle transition.",
                    nameof(restorePoint));
            }

            if (state == OperationState.RestorePointBeginRequested)
            {
                if (previousRestorePoint is not null)
                {
                    throw new ArgumentException("A restore-point begin request must start a new correlation.", nameof(previousRestorePoint));
                }
            }
            else if (previousRestorePoint is null || !restorePoint.Continues(previousRestorePoint))
            {
                throw new ArgumentException("Restore-point facts do not continue the verified prior lifecycle event.", nameof(previousRestorePoint));
            }
        }

        ValidatePlanContext(facts, expectedState, state, restorePoint);

        return new OperationTransition(
            operationId,
            facts,
            expectedRevision,
            expectedState,
            state,
            stepId,
            occurredAtUtc,
            restorePoint,
            previousFacts?.Digest,
            previousRestorePoint?.Digest);
    }

    internal static bool RequiresExactStep(OperationState state) =>
        state is OperationState.Applying or
            OperationState.Applied or
            OperationState.Verified or
            OperationState.ApplyStepNotApplied or
            OperationState.VerificationFailedRollbackOffered or
            OperationState.RollingBack or
            OperationState.RollbackApplied or
            OperationState.RollbackVerified;

    private static bool IsReviewedFinalizationRetry(
        OperationState current,
        OperationState state,
        RestorePointTransitionFacts? restorePoint,
        RestorePointTransitionFacts? previousRestorePoint) =>
        current == OperationState.RestorePointFinalizeFailedRecoveryRequired &&
        previousRestorePoint is { } previous &&
        restorePoint is { } retry &&
        previous.FinalizationApiStatus == RestorePointApiStatus.KnownFailure &&
        retry.FinalizationApiStatus == RestorePointApiStatus.NotCalled &&
        retry.FinalizationMode == previous.FinalizationMode &&
        ((retry.FinalizationMode == RestorePointFinalizationMode.Normal &&
          state == OperationState.RestorePointEndRequested) ||
         (retry.FinalizationMode == RestorePointFinalizationMode.Cancelled &&
          state == OperationState.RestorePointCancelRequested));

    internal static bool IsRestorePointState(OperationState state) =>
        state is OperationState.RestorePointFailedNoChanges or
            OperationState.RestorePointBeginRequested or
            OperationState.RestorePointBeginReturnedUnverified or
            OperationState.RestorePointBegun or
            OperationState.RestorePointEndRequested or
            OperationState.RestorePointCancelRequested or
            OperationState.RestorePointEnded or
            OperationState.RestorePointCancelled or
            OperationState.RestorePointRecoveryRequired or
            OperationState.RestorePointFinalizeFailedRecoveryRequired or
            OperationState.RestorePointFinalizeOutcomeUnknown;

    private static void ValidatePlanContext(
        DurableOperationFacts facts,
        OperationState? expectedState,
        OperationState state,
        RestorePointTransitionFacts? restorePoint)
    {
        if (state == OperationState.Planned &&
            (facts.BackupDigest is not null ||
             facts.RecoveryCheckpointId is not null ||
             facts.RecoveryCheckpointDigest is not null))
        {
            throw new ArgumentException("Planned cannot claim backup or checkpoint facts before they exist.", nameof(state));
        }

        if (state == OperationState.RollbackPlanned &&
            (string.IsNullOrWhiteSpace(facts.BackupDigest) ||
             facts.RecoveryCheckpointId is not null ||
             facts.RecoveryCheckpointDigest is not null))
        {
            throw new ArgumentException(
                "RollbackPlanned requires its linked backup and cannot prepopulate a checkpoint.",
                nameof(state));
        }

        if (state == OperationState.RestorePointBeginRequested)
        {
            var validTargetMutation =
                facts.Kind == ChangePlanKind.TargetMutation &&
                facts.RequiresRestorePoint &&
                facts.Backup == BackupRequirement.Required &&
                !string.IsNullOrWhiteSpace(facts.BackupDigest) &&
                expectedState == OperationState.BackupCreated;
            var validManualArtifact =
                facts.Kind == ChangePlanKind.ManualRestorePointArtifact &&
                facts.Backup == BackupRequirement.NotApplicable &&
                facts.Rollback == RollbackCapability.NotApplicable &&
                facts.BackupDigest is null &&
                expectedState == OperationState.Prepared;
            if (!validTargetMutation && !validManualArtifact)
            {
                throw new ArgumentException("Restore begin does not satisfy the plan's backup and artifact policy.", nameof(state));
            }
        }

        if (state == OperationState.BackupCreated &&
            (facts.Kind != ChangePlanKind.TargetMutation ||
             facts.Backup != BackupRequirement.Required ||
             string.IsNullOrWhiteSpace(facts.BackupDigest)))
        {
            throw new ArgumentException(
                "BackupCreated requires a target mutation and a verified backup digest.",
                nameof(state));
        }

        if (state == OperationState.Applying)
        {
            if (facts.Kind != ChangePlanKind.TargetMutation)
            {
                throw new ArgumentException("A safety artifact cannot enter target mutation states.", nameof(state));
            }

            if (facts.Backup != BackupRequirement.Required || string.IsNullOrWhiteSpace(facts.BackupDigest))
            {
                throw new ArgumentException("Applying requires verified durable backup facts.", nameof(state));
            }

            if (facts.RequiresRestorePoint &&
                (restorePoint is null ||
                 !restorePoint.IsValidFor(OperationState.RestorePointBegun) ||
                 expectedState is not (OperationState.RestorePointBegun or OperationState.Verified)))
            {
                throw new ArgumentException("Applying requires a verified active restore point for this plan.", nameof(state));
            }
        }

        if (facts.RequiresRestorePoint &&
            facts.Kind == ChangePlanKind.TargetMutation &&
            RequiresActiveRestoreLifecycle(state) &&
            (restorePoint is null || !restorePoint.IsValidFor(OperationState.RestorePointBegun)))
        {
            throw new ArgumentException(
                "A restore-required mutation state must retain its verified active lifecycle.",
                nameof(restorePoint));
        }


        if ((state is OperationState.RollbackCheckpointCreated or
             OperationState.RollingBack or
             OperationState.RollbackApplied or
             OperationState.RollbackVerified or
             OperationState.RolledBack) &&
            (string.IsNullOrWhiteSpace(facts.RecoveryCheckpointId) ||
             string.IsNullOrWhiteSpace(facts.RecoveryCheckpointDigest)))
        {
            throw new ArgumentException(
                "Rollback mutation states require a verified durable recovery checkpoint.",
                nameof(state));
        }

        if (state == OperationState.Completed &&
            (facts.RequiresRestorePoint || facts.Kind == ChangePlanKind.ManualRestorePointArtifact) &&
            (restorePoint is null || !restorePoint.IsValidFor(OperationState.RestorePointEnded)))
        {
            throw new ArgumentException("Completion requires a durably ended restore lifecycle.", nameof(state));
        }
    }

    internal static bool RequiresActiveRestoreLifecycle(OperationState state) =>
        state is OperationState.Applying or
            OperationState.Applied or
            OperationState.Verified or
            OperationState.ApplyStepNotApplied or
            OperationState.ApplyFailedNoChanges or
            OperationState.PartiallyAppliedRecoveryRequired or
            OperationState.VerificationFailedRollbackOffered or
            OperationState.RecoveryConflictExternalDrift;

    private static string ComputeHash(OperationTransition transition)
    {
        var canonical = new StringBuilder();
        Append(canonical, transition.OperationId.ToString("D"));
        Append(canonical, transition.Facts.PlanDigest);
        Append(canonical, transition.Facts.Digest);
        Append(canonical, transition.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendOptional(canonical, transition.ExpectedState?.ToString());
        Append(canonical, transition.State.ToString());
        AppendOptional(canonical, transition.StepId);
        Append(canonical, transition.OccurredAtUtc.ToString("O"));
        AppendOptional(canonical, transition.RestorePoint?.Digest);
        AppendOptional(canonical, transition.ExpectedFactsDigest);
        AppendOptional(canonical, transition.ExpectedRestorePointFactsDigest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendOptional(StringBuilder builder, string? value)
    {
        Append(builder, value is null ? "0" : "1");
        if (value is not null)
        {
            Append(builder, value);
        }
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value));
        builder.Append(':');
        builder.Append(value);
    }
}

public sealed record DurableOperationBoundary
{
    private DurableOperationBoundary(
        Guid operationId,
        DurableOperationFacts facts,
        long revision,
        OperationState state,
        string? stepId,
        IReadOnlyList<string> appliedStepIds,
        RestorePointTransitionFacts? restorePoint)
    {
        OperationId = operationId;
        Facts = facts;
        Revision = revision;
        State = state;
        StepId = stepId;
        AppliedStepIds = Array.AsReadOnly(appliedStepIds.ToArray());
        RestorePoint = restorePoint;
    }

    public Guid OperationId { get; }

    public DurableOperationFacts Facts { get; }

    public long Revision { get; }

    public OperationState State { get; }

    public string? StepId { get; }

    public IReadOnlyList<string> AppliedStepIds { get; }

    public RestorePointTransitionFacts? RestorePoint { get; }

    internal static DurableOperationBoundary Create(
        Guid operationId,
        DurableOperationFacts facts,
        long revision,
        OperationState state,
        string? stepId,
        IReadOnlyList<string> appliedStepIds,
        RestorePointTransitionFacts? restorePoint = null)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A durable operation identifier is required.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(appliedStepIds);
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        if (stepId is not null && !facts.OrderedStepIds.Contains(stepId, StringComparer.Ordinal))
        {
            throw new ArgumentException("The boundary step must belong to the durable plan facts.", nameof(stepId));
        }

        if (appliedStepIds.Any(id => !facts.OrderedStepIds.Contains(id, StringComparer.Ordinal)) ||
            appliedStepIds.Distinct(StringComparer.Ordinal).Count() != appliedStepIds.Count)
        {
            throw new ArgumentException("Applied step identifiers must be unique durable plan steps.", nameof(appliedStepIds));
        }

        if (OperationTransition.RequiresExactStep(state) && stepId is null)
        {
            throw new ArgumentException("This durable boundary requires its exact plan step identifier.", nameof(stepId));
        }

        if (state is OperationState.Applying or OperationState.RollingBack)
        {
            var uncertainIndex = FindStepIndex(facts.OrderedStepIds, stepId!);
            var expectedPrefix = facts.OrderedStepIds.Take(uncertainIndex);
            if (!appliedStepIds.SequenceEqual(expectedPrefix, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "Completed steps must be the exact durable prefix before the uncertain mutation step.",
                    nameof(appliedStepIds));
            }
        }

        if (OperationTransition.IsRestorePointState(state))
        {
            if (restorePoint is null || !restorePoint.IsValidFor(state))
            {
                throw new ArgumentException("A restore boundary requires state-consistent restore facts.", nameof(restorePoint));
            }
        }

        if (state == OperationState.BackupCreated &&
            (facts.Kind != ChangePlanKind.TargetMutation ||
             facts.Backup != BackupRequirement.Required ||
             string.IsNullOrWhiteSpace(facts.BackupDigest)))
        {
            throw new ArgumentException("BackupCreated boundary requires verified backup facts.", nameof(facts));
        }

        if (state == OperationState.Applying && facts.RequiresRestorePoint &&
            (restorePoint is null || !restorePoint.IsValidFor(OperationState.RestorePointBegun)))
        {
            throw new ArgumentException("Applying boundary lost the verified active restore point.", nameof(restorePoint));
        }

        if (facts.RequiresRestorePoint &&
            facts.Kind == ChangePlanKind.TargetMutation &&
            OperationTransition.RequiresActiveRestoreLifecycle(state) &&
            (restorePoint is null || !restorePoint.IsValidFor(OperationState.RestorePointBegun)))
        {
            throw new ArgumentException(
                "A restore-required mutation boundary must retain its verified active lifecycle.",
                nameof(restorePoint));
        }

        if ((state is OperationState.Applying or OperationState.Applied or OperationState.Verified) &&
            (facts.Backup != BackupRequirement.Required || string.IsNullOrWhiteSpace(facts.BackupDigest)))
        {
            throw new ArgumentException("Target mutation boundaries require verified backup facts.", nameof(facts));
        }

        if ((state is OperationState.RollbackCheckpointCreated or
             OperationState.RollingBack or
             OperationState.RollbackApplied or
             OperationState.RollbackVerified or
             OperationState.RolledBack) &&
            (string.IsNullOrWhiteSpace(facts.RecoveryCheckpointId) ||
             string.IsNullOrWhiteSpace(facts.RecoveryCheckpointDigest)))
        {
            throw new ArgumentException("Rollback mutation boundaries require verified checkpoint facts.", nameof(facts));
        }

        return new DurableOperationBoundary(operationId, facts, revision, state, stepId, appliedStepIds, restorePoint);
    }

    private static int FindStepIndex(IReadOnlyList<string> stepIds, string stepId)
    {
        for (var index = 0; index < stepIds.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(stepIds[index], stepId))
            {
                return index;
            }
        }

        return -1;
    }
}

public sealed record DurableTransitionResult
{
    private DurableTransitionResult(
        bool isDurable,
        long revision,
        OperationState? state,
        Guid? operationId,
        string? planDigest,
        string? factsDigest,
        string? stepId,
        string? transitionHash)
    {
        IsDurable = isDurable;
        Revision = revision;
        State = state;
        OperationId = operationId;
        PlanDigest = planDigest;
        FactsDigest = factsDigest;
        StepId = stepId;
        TransitionHash = transitionHash;
    }

    public bool IsDurable { get; }

    public long Revision { get; }

    public OperationState? State { get; }

    public Guid? OperationId { get; }

    public string? PlanDigest { get; }

    public string? FactsDigest { get; }

    public string? StepId { get; }

    public string? TransitionHash { get; }

    internal static DurableTransitionResult Acknowledged(
        OperationTransition transition,
        long? durableRevision = null)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return new DurableTransitionResult(
            true,
            durableRevision ?? transition.ExpectedRevision + 1,
            transition.State,
            transition.OperationId,
            transition.Facts.PlanDigest,
            transition.Facts.Digest,
            transition.StepId,
            transition.TransitionHash);
    }

    internal static DurableTransitionResult Rejected(long revision, OperationState? state) =>
        new(false, revision, state, null, null, null, null, null);

    internal bool Acknowledges(OperationTransition transition) =>
        IsDurable &&
        Revision == transition.ExpectedRevision + 1 &&
        State == transition.State &&
        OperationId == transition.OperationId &&
        StringComparer.Ordinal.Equals(PlanDigest, transition.Facts.PlanDigest) &&
        StringComparer.Ordinal.Equals(FactsDigest, transition.Facts.Digest) &&
        StringComparer.Ordinal.Equals(StepId, transition.StepId) &&
        StringComparer.Ordinal.Equals(TransitionHash, transition.TransitionHash);
}

public interface IDurableOperationJournal
{
    /// <summary>
    /// Reads and hash-verifies immutable history, then reconstructs its last durable boundary.
    /// A null result means no verified history exists for the operation.
    /// </summary>
    ValueTask<DurableOperationBoundary?> ReadVerifiedBoundaryAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically compare-and-appends, flushes, reads back, and verifies an immutable transition.
    /// IsDurable is true only after all four actions succeed at exactly ExpectedRevision + 1.
    /// </summary>
    ValueTask<DurableTransitionResult> CompareAndAppendAsync(
        OperationTransition transition,
        CancellationToken cancellationToken);
}
