namespace Winora.Core.Changes;

public enum OperationState
{
    Planned,
    Prepared,
    BackupCreated,
    Applying,
    Applied,
    Verified,
    Completed,
    CanceledNoChanges,
    ElevationCanceledNoChanges,
    Unsupported,
    PlanInvalidatedNoChanges,
    BackupFailedNoChanges,
    RestorePointFailedNoChanges,
    ApplyFailedNoChanges,
    ApplyStepNotApplied,
    PartiallyAppliedRecoveryRequired,
    VerificationFailedRollbackOffered,
    RecoveryConflictExternalDrift,
    RollbackPlanned,
    RollbackPrepared,
    RollbackCheckpointCreated,
    RollingBack,
    RollbackApplied,
    RollbackVerified,
    RolledBack,
    AlreadyRestored,
    RollbackFailedRecoveryRequired,
    RestorePointBeginRequested,
    RestorePointBeginReturnedUnverified,
    RestorePointBegun,
    RestorePointEndRequested,
    RestorePointCancelRequested,
    RestorePointEnded,
    RestorePointCancelled,
    RestorePointRecoveryRequired,
    RestorePointFinalizeFailedRecoveryRequired,
    RestorePointFinalizeOutcomeUnknown,
    OperationBusy,
}

public static class OperationStatePolicy
{
    public static bool IsTerminal(OperationState state) =>
        state is OperationState.Completed or
            OperationState.CanceledNoChanges or
            OperationState.ElevationCanceledNoChanges or
            OperationState.Unsupported or
            OperationState.PlanInvalidatedNoChanges or
            OperationState.BackupFailedNoChanges or
            OperationState.RestorePointFailedNoChanges or
            OperationState.ApplyFailedNoChanges or
            OperationState.RolledBack or
            OperationState.OperationBusy;

    public static bool CanStartWith(OperationState state) =>
        state is OperationState.Planned or OperationState.RollbackPlanned;

    public static bool CanTransition(OperationState from, OperationState to) =>
        (from, to) switch
        {
            (OperationState.Planned, OperationState.Prepared or
                OperationState.CanceledNoChanges or
                OperationState.ElevationCanceledNoChanges or
                OperationState.Unsupported or
                OperationState.PlanInvalidatedNoChanges) => true,

            (OperationState.Prepared, OperationState.BackupCreated or
                OperationState.RestorePointBeginRequested or
                OperationState.CanceledNoChanges or
                OperationState.ElevationCanceledNoChanges or
                OperationState.PlanInvalidatedNoChanges or
                OperationState.BackupFailedNoChanges) => true,

            (OperationState.BackupCreated, OperationState.Applying or
                OperationState.RestorePointBeginRequested or
                OperationState.CanceledNoChanges or
                OperationState.ElevationCanceledNoChanges or
                OperationState.Unsupported or
                OperationState.PlanInvalidatedNoChanges or
                OperationState.RestorePointFailedNoChanges) => true,

            (OperationState.Applying, OperationState.Applied or
                OperationState.ApplyStepNotApplied or
                OperationState.PartiallyAppliedRecoveryRequired or
                OperationState.RecoveryConflictExternalDrift) => true,

            (OperationState.ApplyStepNotApplied, OperationState.ApplyFailedNoChanges or
                OperationState.PartiallyAppliedRecoveryRequired) => true,

            (OperationState.Applied, OperationState.Verified or
                OperationState.VerificationFailedRollbackOffered or
                OperationState.PartiallyAppliedRecoveryRequired or
                OperationState.RecoveryConflictExternalDrift) => true,

            (OperationState.Verified, OperationState.Applying or
                OperationState.Completed or
                OperationState.PartiallyAppliedRecoveryRequired or
                OperationState.RecoveryConflictExternalDrift or
                OperationState.RestorePointEndRequested) => true,

            (OperationState.Completed or
                OperationState.PartiallyAppliedRecoveryRequired or
                OperationState.VerificationFailedRollbackOffered,
                OperationState.RollbackPlanned) => true,

            (OperationState.PartiallyAppliedRecoveryRequired,
                OperationState.RestorePointEndRequested) => true,

            (OperationState.RollbackPlanned, OperationState.RollbackPrepared or
                OperationState.AlreadyRestored or
                OperationState.Unsupported or
                OperationState.RollbackFailedRecoveryRequired or
                OperationState.RecoveryConflictExternalDrift) => true,

            (OperationState.RollbackPrepared, OperationState.RollbackCheckpointCreated or
                OperationState.RollbackFailedRecoveryRequired or
                OperationState.RecoveryConflictExternalDrift) => true,

            (OperationState.RollbackCheckpointCreated, OperationState.RollingBack or
                OperationState.Unsupported or
                OperationState.RollbackFailedRecoveryRequired or
                OperationState.RecoveryConflictExternalDrift) => true,

            (OperationState.RollingBack, OperationState.RollbackApplied or
                OperationState.AlreadyRestored or
                OperationState.RollbackFailedRecoveryRequired or
                OperationState.RecoveryConflictExternalDrift) => true,

            (OperationState.RollbackApplied, OperationState.RollbackVerified or
                OperationState.RollbackFailedRecoveryRequired or
                OperationState.RecoveryConflictExternalDrift) => true,

            (OperationState.RollbackVerified or OperationState.AlreadyRestored,
                OperationState.RollingBack or
                OperationState.RolledBack or
                OperationState.RollbackFailedRecoveryRequired or
                OperationState.RecoveryConflictExternalDrift) => true,

            (OperationState.RestorePointBeginRequested,
                OperationState.RestorePointBeginReturnedUnverified or
                OperationState.RestorePointFailedNoChanges or
                OperationState.RestorePointRecoveryRequired) => true,

            (OperationState.RestorePointBeginReturnedUnverified,
                OperationState.RestorePointBegun or
                OperationState.RestorePointFailedNoChanges or
                OperationState.RestorePointRecoveryRequired) => true,

            (OperationState.RestorePointBegun,
                OperationState.Applying or
                OperationState.RestorePointEndRequested or
                OperationState.RestorePointCancelRequested or
                OperationState.RestorePointRecoveryRequired) => true,

            (OperationState.RestorePointEndRequested,
                OperationState.RestorePointEnded or
                OperationState.RestorePointFinalizeFailedRecoveryRequired or
                OperationState.RestorePointFinalizeOutcomeUnknown) => true,

            (OperationState.RestorePointCancelRequested,
                OperationState.RestorePointCancelled or
                OperationState.RestorePointFinalizeFailedRecoveryRequired or
                OperationState.RestorePointFinalizeOutcomeUnknown) => true,

            (OperationState.RestorePointEnded,
                OperationState.Verified or
                OperationState.Completed or
                OperationState.PartiallyAppliedRecoveryRequired) => true,

            (OperationState.RestorePointCancelled, OperationState.CanceledNoChanges) => true,

            (OperationState.RestorePointFinalizeOutcomeUnknown, OperationState.RestorePointRecoveryRequired) => true,

            _ => false,
        };
}
