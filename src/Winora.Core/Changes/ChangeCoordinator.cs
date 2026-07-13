using Winora.Core.Contracts;

namespace Winora.Core.Changes;

public enum CoordinatorDisposition
{
    Completed,
    Canceled,
    Blocked,
    Invalidated,
    BackupFailed,
    ApplyFailed,
    PartialRecoveryRequired,
    VerificationFailed,
    DurabilityFailure,
    OperationBusy,
    Reconciled,
    Conflict,
    RolledBack,
    AlreadyRestored,
    RollbackFailed,
}

public sealed record CoordinatorResult(
    CoordinatorDisposition Disposition,
    OperationState? DurableState,
    long DurableRevision,
    int VerifiedStepCount,
    string? Detail);

public sealed record ApplyingRecovery(
    ChangePlan Plan,
    ChangeStep Step);

public sealed record RollingBackRecovery(
    RollbackPlan Plan,
    ChangeStep Step);

public sealed partial class ChangeCoordinator
{
    private readonly IDurableOperationJournal _journal;
    private readonly IBackupRepository _backups;
    private readonly IMutationLease _mutationLease;
    private readonly IClock _clock;
    private readonly ConfirmationAuthority _confirmationAuthority;

    public ChangeCoordinator(
        IDurableOperationJournal journal,
        IBackupRepository backups,
        IMutationLease mutationLease,
        IClock clock,
        ConfirmationAuthority confirmationAuthority)
    {
        _journal = journal;
        _backups = backups;
        _mutationLease = mutationLease;
        _clock = clock;
        _confirmationAuthority = confirmationAuthority;
    }

    public async ValueTask<CoordinatorResult> ApplyAsync(
        IOperation operation,
        ChangePlan plan,
        ConfirmationToken confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmation);

        if (!_confirmationAuthority.TryAuthorize(confirmation, plan))
        {
            return Result(CoordinatorDisposition.Blocked, new Cursor(), "Confirmation does not match the plan.");
        }

        var lease = await _mutationLease.TryAcquireAsync(plan.PlanId, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return Result(CoordinatorDisposition.OperationBusy, new Cursor(), "Another mutation holds the lease.");
        }

        await using var leaseScope = lease;
        var cursor = new Cursor();
        var facts = DurableOperationFacts.From(plan);
        if (!await MoveAsync(plan.PlanId, facts, cursor, OperationState.Planned, null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        if (!StringComparer.Ordinal.Equals(operation.OperationId, plan.OperationId) ||
            operation is not IConditionalSystemMutation conditionalMutation ||
            string.IsNullOrWhiteSpace(conditionalMutation.ConditionalMutationMechanismId))
        {
            if (!await MoveAsync(plan.PlanId, facts, cursor, OperationState.Unsupported, null).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(CoordinatorDisposition.Blocked, cursor, "The operation lacks a conditional mutation contract.");
        }

        var requiresRestoreLifecycle =
            plan.RequiresRestorePoint ||
            plan.Kind == ChangePlanKind.ManualRestorePointArtifact;
        if (requiresRestoreLifecycle)
        {
            if (!await MoveAsync(plan.PlanId, facts, cursor, OperationState.Unsupported, null).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(
                CoordinatorDisposition.Blocked,
                cursor,
                "This plan requires the dedicated System Restore lifecycle coordinator.");
        }

        OperationCapability capability;
        try
        {
            capability = await operation.ProbeAsync(
                new OperationTarget(plan.OperationId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CancelBeforeMutationAsync(plan.PlanId, facts, cursor).ConfigureAwait(false);
        }

        var safety = ChangeSafetyPolicy.Evaluate(plan, capability);
        if (!safety.IsAllowed)
        {
            if (!await MoveAsync(plan.PlanId, facts, cursor, OperationState.Unsupported, null).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor);
            }

            return Result(CoordinatorDisposition.Blocked, cursor, safety.BlockReason);
        }

        if (capability.CurrentFingerprint != plan.SourceFingerprint)
        {
            return await InvalidateBeforeMutationAsync(plan.PlanId, facts, cursor).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await CancelBeforeMutationAsync(plan.PlanId, facts, cursor).ConfigureAwait(false);
        }

        if (!await MoveAsync(plan.PlanId, facts, cursor, OperationState.Prepared, null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        BackupReceipt receipt;
        try
        {
            receipt = await _backups.CreateAndVerifyAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CancelBeforeMutationAsync(plan.PlanId, facts, cursor).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await BackupFailedAsync(plan.PlanId, facts, cursor, exception.Message).ConfigureAwait(false);
        }

        if (!ReceiptMatches(
                receipt,
                plan.Digest,
                plan.SourceFingerprint,
                plan.SourceFingerprint))
        {
            return await BackupFailedAsync(
                plan.PlanId,
                facts,
                cursor,
                "The verified backup is not bound to the confirmed source and plan.").ConfigureAwait(false);
        }

        facts = facts.WithBackupDigest(receipt.BackupDigest);

        if (!await MoveAsync(plan.PlanId, facts, cursor, OperationState.BackupCreated, null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await CancelBeforeMutationAsync(plan.PlanId, facts, cursor).ConfigureAwait(false);
        }

        var verifiedStepCount = 0;
        foreach (var step in plan.Steps)
        {
            if (verifiedStepCount > 0 && cancellationToken.IsCancellationRequested)
            {
                if (!await MoveAsync(
                        plan.PlanId,
                        facts,
                        cursor,
                        OperationState.PartiallyAppliedRecoveryRequired,
                        step.StepId).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                return Result(
                    CoordinatorDisposition.PartialRecoveryRequired,
                    cursor,
                    "An emergency stop was honored between steps.",
                    verifiedStepCount);
            }

            OperationCapability stepCapability;
            try
            {
                stepCapability = await operation.ProbeAsync(
                    step.Target,
                    verifiedStepCount == 0 ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await CancelBeforeMutationAsync(plan.PlanId, facts, cursor).ConfigureAwait(false);
            }

            var stepSafety = ChangeSafetyPolicy.Evaluate(plan, stepCapability);
            if (!stepSafety.IsAllowed)
            {
                var blockedState = verifiedStepCount == 0
                    ? OperationState.Unsupported
                    : OperationState.PartiallyAppliedRecoveryRequired;
                if (!await MoveAsync(
                        plan.PlanId,
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
                        : CoordinatorDisposition.PartialRecoveryRequired,
                    cursor,
                    stepSafety.BlockReason,
                    verifiedStepCount);
            }

            if (stepCapability.CurrentFingerprint != step.SourceFingerprint)
            {
                var driftState = verifiedStepCount == 0
                    ? OperationState.PlanInvalidatedNoChanges
                    : OperationState.RecoveryConflictExternalDrift;
                if (!await MoveAsync(plan.PlanId, facts, cursor, driftState, step.StepId).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                return Result(
                    verifiedStepCount == 0
                        ? CoordinatorDisposition.Invalidated
                        : CoordinatorDisposition.Conflict,
                    cursor,
                    "The step fingerprint changed before conditional apply.",
                    verifiedStepCount);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (verifiedStepCount == 0)
                {
                    return await CancelBeforeMutationAsync(plan.PlanId, facts, cursor).ConfigureAwait(false);
                }

                if (!await MoveAsync(
                        plan.PlanId,
                        facts,
                        cursor,
                        OperationState.PartiallyAppliedRecoveryRequired,
                        step.StepId).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                return Result(
                    CoordinatorDisposition.PartialRecoveryRequired,
                    cursor,
                    "An emergency stop was honored between steps.",
                    verifiedStepCount);
            }

            if (!await MoveAsync(
                    plan.PlanId,
                    facts,
                    cursor,
                    OperationState.Applying,
                    step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor, verifiedStepCount);
            }

            var stepResult = await operation.ApplyStepAsync(
                plan,
                step,
                CancellationToken.None).ConfigureAwait(false);

            if (stepResult.Kind != StepResultKind.Applied)
            {
                if (stepResult.Kind == StepResultKind.FailedOutcomeUnknown)
                {
                    if (!await MoveAsync(
                            plan.PlanId,
                            facts,
                            cursor,
                            OperationState.PartiallyAppliedRecoveryRequired,
                            step.StepId).ConfigureAwait(false))
                    {
                        return DurabilityFailure(cursor, verifiedStepCount);
                    }

                    return Result(
                        CoordinatorDisposition.PartialRecoveryRequired,
                        cursor,
                        stepResult.Detail,
                        verifiedStepCount);
                }

                if (!await MoveAsync(
                        plan.PlanId,
                        facts,
                        cursor,
                        OperationState.ApplyStepNotApplied,
                        step.StepId).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                var terminal = verifiedStepCount == 0
                    ? OperationState.ApplyFailedNoChanges
                    : OperationState.PartiallyAppliedRecoveryRequired;
                if (!await MoveAsync(plan.PlanId, facts, cursor, terminal, step.StepId).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                return Result(
                    verifiedStepCount == 0
                        ? CoordinatorDisposition.ApplyFailed
                        : CoordinatorDisposition.PartialRecoveryRequired,
                    cursor,
                    stepResult.Detail,
                    verifiedStepCount);
            }

            if (!await MoveAsync(
                    plan.PlanId,
                    facts,
                    cursor,
                    OperationState.Applied,
                    step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor, verifiedStepCount);
            }

            var verification = await operation.VerifyStepAsync(
                plan,
                step,
                CancellationToken.None).ConfigureAwait(false);
            if (!verification.IsVerified || verification.ObservedFingerprint != step.ResultFingerprint)
            {
                if (!await MoveAsync(
                        plan.PlanId,
                        facts,
                        cursor,
                        OperationState.VerificationFailedRollbackOffered,
                        step.StepId).ConfigureAwait(false))
                {
                    return DurabilityFailure(cursor, verifiedStepCount);
                }

                return Result(
                    CoordinatorDisposition.VerificationFailed,
                    cursor,
                    verification.Detail,
                    verifiedStepCount);
            }

            if (!await MoveAsync(
                    plan.PlanId,
                    facts,
                    cursor,
                    OperationState.Verified,
                    step.StepId).ConfigureAwait(false))
            {
                return DurabilityFailure(cursor, verifiedStepCount);
            }

            verifiedStepCount++;
        }

        if (!await MoveAsync(plan.PlanId, facts, cursor, OperationState.Completed, null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor, verifiedStepCount);
        }

        return Result(CoordinatorDisposition.Completed, cursor, null, verifiedStepCount);
    }

    private async ValueTask<bool> MoveAsync(
        Guid operationId,
        DurableOperationFacts facts,
        Cursor cursor,
        OperationState state,
        string? stepId)
    {
        if (cursor.State is { } current)
        {
            if (!OperationStatePolicy.CanTransition(current, state))
            {
                return false;
            }
        }
        else if (!OperationStatePolicy.CanStartWith(state))
        {
            return false;
        }

        OperationTransition transition;
        DurableTransitionResult persisted;
        try
        {
            transition = OperationTransition.Create(
                operationId,
                facts,
                cursor.Revision,
                cursor.State,
                state,
                stepId,
                _clock.UtcNow,
                cursor.RestorePoint,
                cursor.RestorePoint,
                previousFacts: cursor.Facts);
            persisted = await _journal.CompareAndAppendAsync(
                transition,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        if (!persisted.Acknowledges(transition))
        {
            return false;
        }

        cursor.State = state;
        cursor.Revision = persisted.Revision;
        cursor.Facts = facts;
        return true;
    }

    private async ValueTask<CoordinatorResult> CancelBeforeMutationAsync(
        Guid operationId,
        DurableOperationFacts facts,
        Cursor cursor)
    {
        if (!await MoveAsync(
                operationId,
                facts,
                cursor,
                OperationState.CanceledNoChanges,
                null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        return Result(CoordinatorDisposition.Canceled, cursor, null);
    }

    private async ValueTask<CoordinatorResult> InvalidateBeforeMutationAsync(
        Guid operationId,
        DurableOperationFacts facts,
        Cursor cursor)
    {
        if (!await MoveAsync(
                operationId,
                facts,
                cursor,
                OperationState.PlanInvalidatedNoChanges,
                null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        return Result(CoordinatorDisposition.Invalidated, cursor, "The source fingerprint changed.");
    }

    private async ValueTask<CoordinatorResult> BackupFailedAsync(
        Guid operationId,
        DurableOperationFacts facts,
        Cursor cursor,
        string? detail)
    {
        if (!await MoveAsync(
                operationId,
                facts,
                cursor,
                OperationState.BackupFailedNoChanges,
                null).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor);
        }

        return Result(CoordinatorDisposition.BackupFailed, cursor, detail);
    }

    private async ValueTask<CoordinatorResult> RollbackFailedAsync(
        Guid operationId,
        DurableOperationFacts facts,
        Cursor cursor,
        string? detail,
        int verifiedStepCount = 0,
        string? stepId = null)
    {
        if (!await MoveAsync(
                operationId,
                facts,
                cursor,
                OperationState.RollbackFailedRecoveryRequired,
                stepId).ConfigureAwait(false))
        {
            return DurabilityFailure(cursor, verifiedStepCount);
        }

        return Result(CoordinatorDisposition.RollbackFailed, cursor, detail, verifiedStepCount);
    }

    private static bool ReceiptMatches(
        BackupReceipt receipt,
        string digest,
        StateFingerprint captured,
        StateFingerprint live) =>
        receipt.IsVerified &&
        !string.IsNullOrWhiteSpace(receipt.BackupId) &&
        !string.IsNullOrWhiteSpace(receipt.BackupDigest) &&
        StringComparer.Ordinal.Equals(receipt.PlanDigest, digest) &&
        receipt.CapturedSourceFingerprint == captured &&
        receipt.LiveSourceFingerprint == live;

    private static CoordinatorResult DurabilityFailure(Cursor cursor, int verifiedStepCount = 0) =>
        Result(
            CoordinatorDisposition.DurabilityFailure,
            cursor,
            "The next transition was not durably compare-and-appended.",
            verifiedStepCount);

    private static CoordinatorResult Result(
        CoordinatorDisposition disposition,
        Cursor cursor,
        string? detail,
        int verifiedStepCount = 0) =>
        new(disposition, cursor.State, cursor.Revision, verifiedStepCount, detail);

    private sealed class Cursor
    {
        internal Cursor(
            OperationState? state = null,
            long revision = 0,
            DurableOperationFacts? facts = null,
            RestorePointTransitionFacts? restorePoint = null)
        {
            State = state;
            Revision = revision;
            Facts = facts;
            RestorePoint = restorePoint;
        }

        internal OperationState? State { get; set; }

        internal long Revision { get; set; }

        internal DurableOperationFacts? Facts { get; set; }

        internal RestorePointTransitionFacts? RestorePoint { get; set; }
    }
}
