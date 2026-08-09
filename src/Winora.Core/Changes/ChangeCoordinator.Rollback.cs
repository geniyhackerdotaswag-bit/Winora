using Winora.Core.Contracts;

namespace Winora.Core.Changes;

public sealed partial class ChangeCoordinator
{
    public async ValueTask<CoordinatorResult> RollbackAsync(
        IOperation operation,
        RollbackPlan plan,
        RollbackConfirmationToken confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmation);

        if (!_confirmationAuthority.TryAuthorize(confirmation, plan))
        {
            return Result(CoordinatorDisposition.Blocked, new Cursor(), "Confirmation does not match rollback.");
        }

        var lease = await _mutationLease.TryAcquireAsync(plan.RollbackId, cancellationToken).ConfigureAwait(false);

        // An operation left incomplete blocks every ordinary acquisition, and rolling it back is the
        // only transition the state machine allows out of that state. Asking for an ordinary lease
        // therefore deadlocked: the guard refused the one action that could clear what it was
        // guarding against. Fall back to taking recovery ownership of that operation.
        //
        // This does not widen the guard. The lease grants recovery ownership only when exactly one
        // operation is incomplete and the caller names it, so an unrelated rollback still gets
        // nothing and reports OperationBusy as before.
        var isRecoveryTakeover = false;
        if (lease is null)
        {
            lease = await _mutationLease
                .TryAcquireRecoveryAsync(plan.ChangePlan.PlanId, cancellationToken)
                .ConfigureAwait(false);
            isRecoveryTakeover = lease is not null;
        }

        if (lease is null)
        {
            return Result(CoordinatorDisposition.OperationBusy, new Cursor(), "Another mutation holds the lease.");
        }

        await using var leaseScope = lease;

        // A recovery lease is bound to the incomplete operation, not to the new rollback record.
        var expectedLeaseOperationId = isRecoveryTakeover ? plan.ChangePlan.PlanId : plan.RollbackId;
        if (!IsValidLeaseBinding(lease, expectedLeaseOperationId, isRecoveryTakeover) ||
            !await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result(
                CoordinatorDisposition.Blocked,
                new Cursor(),
                "The mutation lease is not bound to this rollback operation.");
        }

        var cursor = new Cursor(steps: plan.Steps, isRollback: true);
        var facts = DurableOperationFacts.From(plan);
        if (!await MoveAsync(plan.RollbackId, facts, cursor, OperationState.RollbackPlanned, null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        var requiresRestoreLifecycle =
            plan.ChangePlan.RequiresRestorePoint ||
            plan.ChangePlan.Kind == ChangePlanKind.ManualRestorePointArtifact;
        if (requiresRestoreLifecycle)
        {
            if (!await MoveAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.Unsupported,
                    null).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(
                CoordinatorDisposition.Blocked,
                cursor,
                "This rollback requires the dedicated System Restore lifecycle coordinator.");
        }

        if (!StringComparer.Ordinal.Equals(operation.OperationId, plan.ChangePlan.OperationId) ||
            operation is not IConditionalSystemMutation conditionalMutation ||
            string.IsNullOrWhiteSpace(conditionalMutation.ConditionalMutationMechanismId))
        {
            if (!await MoveAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.Unsupported,
                    null).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(
                CoordinatorDisposition.Blocked,
                cursor,
                "Rollback does not match a conditional operation adapter.");
        }

        BackupReceipt linkedBackup;
        try
        {
            linkedBackup = await _backups.ReadAndVerifyAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await RollbackFailedAsync(
                plan.RollbackId,
                facts,
                cursor,
                exception.Message).ConfigureAwait(false);
        }

        if (!ReceiptMatches(
                linkedBackup,
                plan.ChangePlan.Digest,
                plan.ChangePlan.SourceFingerprint,
                plan.ChangePlan.SourceFingerprint) ||
            !StringComparer.Ordinal.Equals(linkedBackup.BackupId, plan.BackupId) ||
            !StringComparer.Ordinal.Equals(linkedBackup.BackupDigest, plan.BackupDigest) ||
            linkedBackup.CapturedSourceFingerprint != plan.BackupFingerprint ||
            linkedBackup.LiveSourceFingerprint != plan.BackupFingerprint)
        {
            return await RollbackFailedAsync(
                plan.RollbackId,
                facts,
                cursor,
                "The linked backup could not be read and verified.").ConfigureAwait(false);
        }

        var capability = await operation.ProbeAsync(
            new OperationTarget(plan.ChangePlan.OperationId),
            cancellationToken).ConfigureAwait(false);

        var safety = ChangeSafetyPolicy.Evaluate(plan.ChangePlan, capability);
        if (!safety.IsAllowed)
        {
            if (!await MoveAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.Unsupported,
                    null).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(CoordinatorDisposition.Blocked, cursor, safety.BlockReason);
        }

        if (capability.CurrentFingerprint == plan.BackupFingerprint)
        {
            if (!await MoveAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.AlreadyRestored,
                    null,
                    capability.CurrentFingerprint).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            if (!await MoveAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.RolledBack,
                    null).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(CoordinatorDisposition.AlreadyRestored, cursor, null);
        }

        if (capability.CurrentFingerprint != plan.AppliedFingerprint)
        {
            if (!await MoveAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.RecoveryConflictExternalDrift,
                    null,
                    capability.CurrentFingerprint).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(CoordinatorDisposition.Conflict, cursor, "Rollback preflight found external drift.");
        }

        if (!await MoveAsync(
                plan.RollbackId,
                facts,
                cursor,
                OperationState.RollbackPrepared,
                null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        BackupReceipt checkpoint;
        try
        {
            checkpoint = await _backups.CreateRecoveryCheckpointAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await RollbackFailedAsync(plan.RollbackId, facts, cursor, exception.Message).ConfigureAwait(false);
        }

        if (!ReceiptMatches(
                checkpoint,
                plan.Digest,
                plan.AppliedFingerprint,
                plan.AppliedFingerprint))
        {
            return await RollbackFailedAsync(
                plan.RollbackId,
                facts,
                cursor,
                "The rollback checkpoint could not be verified.").ConfigureAwait(false);
        }

        facts = facts.WithRecoveryCheckpoint(checkpoint.BackupId, checkpoint.BackupDigest);

        if (!await MoveAsync(
                plan.RollbackId,
                facts,
                cursor,
                OperationState.RollbackCheckpointCreated,
                null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        var verifiedStepCount = 0;
        foreach (var step in plan.Steps)
        {
            var before = await operation.ProbeAsync(step.Target, CancellationToken.None).ConfigureAwait(false);
            var stepSafety = ChangeSafetyPolicy.Evaluate(plan.ChangePlan, before);
            if (!stepSafety.IsAllowed)
            {
                var blockedState = verifiedStepCount == 0
                    ? OperationState.Unsupported
                    : OperationState.RollbackFailedRecoveryRequired;
                if (!await MoveAsync(
                        plan.RollbackId,
                        facts,
                        cursor,
                        blockedState,
                        step.StepId).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                return Result(
                    verifiedStepCount == 0
                        ? CoordinatorDisposition.Blocked
                        : CoordinatorDisposition.RollbackFailed,
                    cursor,
                    stepSafety.BlockReason,
                    verifiedStepCount);
            }

            if (before.CurrentFingerprint == step.SourceFingerprint)
            {
                if (!await MoveAsync(
                        plan.RollbackId,
                        facts,
                        cursor,
                        OperationState.RollingBack,
                        step.StepId).ConfigureAwait(false) ||
                    !await MoveAsync(
                        plan.RollbackId,
                        facts,
                        cursor,
                        OperationState.AlreadyRestored,
                        step.StepId,
                        before.CurrentFingerprint).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                verifiedStepCount++;
                continue;
            }

            if (before.CurrentFingerprint != step.ResultFingerprint)
            {
                if (!await MoveAsync(
                        plan.RollbackId,
                        facts,
                        cursor,
                        OperationState.RecoveryConflictExternalDrift,
                        step.StepId,
                        before.CurrentFingerprint).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                return Result(
                    CoordinatorDisposition.Conflict,
                    cursor,
                    "A rollback step found external drift.",
                    verifiedStepCount);
            }

            if (!await MoveAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.RollingBack,
                    step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor, verifiedStepCount);
            }

            var rollback = await operation.RollbackStepAsync(plan, step, CancellationToken.None).ConfigureAwait(false);
            if (rollback.Kind == StepResultKind.AlreadyRestored)
            {
                if (rollback.ObservedFingerprint != step.SourceFingerprint)
                {
                    if (!await MoveAsync(
                            plan.RollbackId,
                            facts,
                            cursor,
                            OperationState.RecoveryConflictExternalDrift,
                            step.StepId,
                            rollback.ObservedFingerprint).ConfigureAwait(false))
                    {
                        return DurabilityFailure(cursor, verifiedStepCount);
                    }

                    return Result(
                        CoordinatorDisposition.Conflict,
                        cursor,
                        "The conditional rollback did not observe the backup fingerprint.",
                        verifiedStepCount);
                }

                if (!await MoveAsync(
                        plan.RollbackId,
                        facts,
                        cursor,
                        OperationState.AlreadyRestored,
                        step.StepId,
                        rollback.ObservedFingerprint).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                verifiedStepCount++;
                continue;
            }

            if (rollback.Kind != StepResultKind.Applied)
            {
                return await RollbackFailedAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    rollback.Detail,
                    verifiedStepCount,
                    step.StepId).ConfigureAwait(false);
            }

            if (!await MoveAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.RollbackApplied,
                    step.StepId,
                    rollback.ObservedFingerprint).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor, verifiedStepCount);
            }

            var after = await operation.ProbeAsync(step.Target, CancellationToken.None).ConfigureAwait(false);
            if (after.CurrentFingerprint != step.SourceFingerprint)
            {
                if (!await MoveAsync(
                        plan.RollbackId,
                        facts,
                        cursor,
                        OperationState.RecoveryConflictExternalDrift,
                        step.StepId,
                        after.CurrentFingerprint).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                return Result(CoordinatorDisposition.Conflict, cursor, "Rollback verification drifted.", verifiedStepCount);
            }

            if (!await MoveAsync(
                    plan.RollbackId,
                    facts,
                    cursor,
                    OperationState.RollbackVerified,
                    step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor, verifiedStepCount);
            }

            verifiedStepCount++;
        }

        if (!await MoveAsync(
                plan.RollbackId,
                facts,
                cursor,
                OperationState.RolledBack,
                null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor, verifiedStepCount);
        }

        return Result(CoordinatorDisposition.RolledBack, cursor, null, verifiedStepCount);
    }
}
