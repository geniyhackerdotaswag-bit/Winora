using Winora.Core.Contracts;

namespace Winora.Core.Changes;

public sealed partial class ChangeCoordinator
{
    public async ValueTask<CoordinatorResult> ReconcileApplyingAsync(
        IOperation operation,
        ApplyingRecovery recovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(recovery);

        if (!StringComparer.Ordinal.Equals(operation.OperationId, recovery.Plan.OperationId) ||
            !recovery.Plan.Steps.Contains(recovery.Step))
        {
            return Result(
                CoordinatorDisposition.Blocked,
                new Cursor(OperationState.Applying),
                "Recovery does not match a canonical plan step.");
        }

        var lease = await _mutationLease.TryAcquireAsync(recovery.Plan.PlanId, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return Result(CoordinatorDisposition.OperationBusy, new Cursor(), "Another mutation holds the lease.");
        }

        await using var leaseScope = lease;
        DurableOperationBoundary? boundary;
        try
        {
            boundary = await _journal.ReadVerifiedBoundaryAsync(
                recovery.Plan.PlanId,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            boundary = null;
        }

        if (!IsApplyingBoundary(boundary, recovery))
        {
            return Result(
                CoordinatorDisposition.Blocked,
                boundary is null ? new Cursor() : new Cursor(boundary.State, boundary.Revision),
                "The durable applying boundary could not be verified.");
        }

        var verifiedBoundary = boundary!;
        var cursor = new Cursor(
            verifiedBoundary.State,
            verifiedBoundary.Revision,
            verifiedBoundary.Facts,
            verifiedBoundary.RestorePoint);
        var facts = verifiedBoundary.Facts;
        var anyPriorStepApplied = verifiedBoundary.AppliedStepIds.Count > 0;
        var observed = await operation.ProbeAsync(recovery.Step.Target, cancellationToken).ConfigureAwait(false);

        if (observed.CurrentFingerprint == recovery.Step.ResultFingerprint)
        {
            if (!await MoveAsync(
                    recovery.Plan.PlanId,
                    facts,
                    cursor,
                    OperationState.Applied,
                    recovery.Step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            var verification = await operation.VerifyStepAsync(
                recovery.Plan,
                recovery.Step,
                CancellationToken.None).ConfigureAwait(false);
            if (!verification.IsVerified || verification.ObservedFingerprint != recovery.Step.ResultFingerprint)
            {
                if (!await MoveAsync(
                        recovery.Plan.PlanId,
                        facts,
                        cursor,
                        OperationState.VerificationFailedRollbackOffered,
                        recovery.Step.StepId).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor);
                }

                return Result(CoordinatorDisposition.VerificationFailed, cursor, verification.Detail);
            }

            if (!await MoveAsync(
                    recovery.Plan.PlanId,
                    facts,
                    cursor,
                    OperationState.Verified,
                    recovery.Step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(CoordinatorDisposition.Reconciled, cursor, null, 1);
        }

        if (observed.CurrentFingerprint == recovery.Step.SourceFingerprint)
        {
            if (!await MoveAsync(
                    recovery.Plan.PlanId,
                    facts,
                    cursor,
                    OperationState.ApplyStepNotApplied,
                    recovery.Step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            var state = anyPriorStepApplied
                ? OperationState.PartiallyAppliedRecoveryRequired
                : OperationState.ApplyFailedNoChanges;
            if (!await MoveAsync(
                    recovery.Plan.PlanId,
                    facts,
                    cursor,
                    state,
                    recovery.Step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(
                anyPriorStepApplied
                    ? CoordinatorDisposition.PartialRecoveryRequired
                    : CoordinatorDisposition.ApplyFailed,
                cursor,
                null);
        }

        if (!await MoveAsync(
                recovery.Plan.PlanId,
                facts,
                cursor,
                OperationState.RecoveryConflictExternalDrift,
                recovery.Step.StepId).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        return Result(CoordinatorDisposition.Conflict, cursor, "Uncertain apply found a third state.");
    }

    public async ValueTask<CoordinatorResult> ReconcileRollingBackAsync(
        IOperation operation,
        RollingBackRecovery recovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(recovery);

        if (!StringComparer.Ordinal.Equals(operation.OperationId, recovery.Plan.ChangePlan.OperationId) ||
            !recovery.Plan.Steps.Contains(recovery.Step))
        {
            return Result(
                CoordinatorDisposition.Blocked,
                new Cursor(OperationState.RollingBack),
                "Recovery does not match a canonical rollback step.");
        }

        var lease = await _mutationLease.TryAcquireAsync(recovery.Plan.RollbackId, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return Result(CoordinatorDisposition.OperationBusy, new Cursor(), "Another mutation holds the lease.");
        }

        await using var leaseScope = lease;
        DurableOperationBoundary? boundary;
        try
        {
            boundary = await _journal.ReadVerifiedBoundaryAsync(
                recovery.Plan.RollbackId,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            boundary = null;
        }

        if (!IsRollingBackBoundary(boundary, recovery))
        {
            return Result(
                CoordinatorDisposition.Blocked,
                boundary is null ? new Cursor() : new Cursor(boundary.State, boundary.Revision),
                "The durable rolling-back boundary could not be verified.");
        }

        var verifiedBoundary = boundary!;
        var cursor = new Cursor(
            verifiedBoundary.State,
            verifiedBoundary.Revision,
            verifiedBoundary.Facts,
            verifiedBoundary.RestorePoint);
        var facts = verifiedBoundary.Facts;
        var observed = await operation.ProbeAsync(recovery.Step.Target, cancellationToken).ConfigureAwait(false);

        if (observed.CurrentFingerprint == recovery.Step.SourceFingerprint)
        {
            if (!await MoveAsync(
                    recovery.Plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.RollbackApplied,
                    recovery.Step.StepId).ConfigureAwait(false) ||
                !await MoveAsync(
                    recovery.Plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.RollbackVerified,
                    recovery.Step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(CoordinatorDisposition.Reconciled, cursor, null, 1);
        }

        if (observed.CurrentFingerprint == recovery.Step.ResultFingerprint)
        {
            if (!await MoveAsync(
                    recovery.Plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.RollbackFailedRecoveryRequired,
                    recovery.Step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(CoordinatorDisposition.RollbackFailed, cursor, "The uncertain rollback did not commit.");
        }

        if (!await MoveAsync(
                recovery.Plan.RollbackId,
                facts,
                cursor,
                OperationState.RecoveryConflictExternalDrift,
                recovery.Step.StepId).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        return Result(CoordinatorDisposition.Conflict, cursor, "Uncertain rollback found a third state.");
    }

    private static bool IsApplyingBoundary(DurableOperationBoundary? boundary, ApplyingRecovery recovery)
    {
        if (boundary is null ||
            boundary.OperationId != recovery.Plan.PlanId ||
            boundary.State != OperationState.Applying ||
            !StringComparer.Ordinal.Equals(boundary.StepId, recovery.Step.StepId) ||
            !StringComparer.Ordinal.Equals(boundary.Facts.PlanDigest, recovery.Plan.Digest) ||
            string.IsNullOrWhiteSpace(boundary.Facts.BackupDigest) ||
            !boundary.Facts.OrderedStepIds.SequenceEqual(
                recovery.Plan.Steps.Select(step => step.StepId),
                StringComparer.Ordinal))
        {
            return false;
        }

        var uncertainIndex = FindStepIndex(boundary.Facts.OrderedStepIds, recovery.Step.StepId);
        return boundary.AppliedStepIds.All(
            id => FindStepIndex(boundary.Facts.OrderedStepIds, id) is var appliedIndex &&
                  appliedIndex >= 0 &&
                  appliedIndex < uncertainIndex);
    }

    private static bool IsRollingBackBoundary(DurableOperationBoundary? boundary, RollingBackRecovery recovery)
    {
        if (boundary is null ||
            boundary.OperationId != recovery.Plan.RollbackId ||
            boundary.State != OperationState.RollingBack ||
            !StringComparer.Ordinal.Equals(boundary.StepId, recovery.Step.StepId) ||
            !StringComparer.Ordinal.Equals(boundary.Facts.PlanDigest, recovery.Plan.Digest) ||
            !StringComparer.Ordinal.Equals(boundary.Facts.BackupDigest, recovery.Plan.BackupDigest) ||
            string.IsNullOrWhiteSpace(boundary.Facts.RecoveryCheckpointId) ||
            string.IsNullOrWhiteSpace(boundary.Facts.RecoveryCheckpointDigest) ||
            !boundary.Facts.OrderedStepIds.SequenceEqual(
                recovery.Plan.Steps.Select(step => step.StepId),
                StringComparer.Ordinal))
        {
            return false;
        }

        var uncertainIndex = FindStepIndex(boundary.Facts.OrderedStepIds, recovery.Step.StepId);
        return uncertainIndex >= 0 && boundary.AppliedStepIds.SequenceEqual(
            boundary.Facts.OrderedStepIds.Take(uncertainIndex),
            StringComparer.Ordinal);
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
