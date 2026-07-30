using Winora.App.Navigation;
using Winora.Core.Changes;

namespace Winora.App.ViewModels;

/// <summary>
/// Maps every coordinator outcome to a distinct user-facing message and a real destination.
/// The mapping is exhaustive on purpose: an unhandled disposition would present as a button that
/// appears to do nothing, which the interaction contract forbids. Backup, apply, verification, and
/// rollback failures stay separate stages and are never collapsed into one generic error.
/// </summary>
public static class CoordinatorDispositionPresentation
{
    public static string ResourceKeyFor(CoordinatorDisposition disposition) => disposition switch
    {
        CoordinatorDisposition.Completed => "Result_Completed",
        CoordinatorDisposition.Canceled => "Result_Canceled",
        CoordinatorDisposition.Blocked => "Result_Blocked",
        CoordinatorDisposition.Invalidated => "Result_Invalidated",
        CoordinatorDisposition.BackupFailed => "Result_BackupFailed",
        CoordinatorDisposition.ApplyFailed => "Result_ApplyFailed",
        CoordinatorDisposition.PartialRecoveryRequired => "Result_PartialRecoveryRequired",
        CoordinatorDisposition.VerificationFailed => "Result_VerificationFailed",
        CoordinatorDisposition.DurabilityFailure => "Result_DurabilityFailure",
        CoordinatorDisposition.OperationBusy => "Result_OperationBusy",
        CoordinatorDisposition.Reconciled => "Result_Reconciled",
        CoordinatorDisposition.Conflict => "Result_Conflict",
        CoordinatorDisposition.RolledBack => "Result_RolledBack",
        CoordinatorDisposition.AlreadyRestored => "Result_AlreadyRestored",
        CoordinatorDisposition.RollbackFailed => "Result_RollbackFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unmapped coordinator disposition."),
    };

    public static string RouteFor(CoordinatorDisposition disposition) => disposition switch
    {
        CoordinatorDisposition.Completed => RouteKeys.ResultSuccess,
        CoordinatorDisposition.RolledBack => RouteKeys.ResultSuccess,
        CoordinatorDisposition.AlreadyRestored => RouteKeys.ResultSuccess,
        CoordinatorDisposition.Reconciled => RouteKeys.ResultSuccess,

        // Nothing was written and no UAC was raised: another Winora session holds the lease, so this
        // is informational and belongs on the in-progress surface, not a failure screen.
        CoordinatorDisposition.OperationBusy => RouteKeys.Applying,

        // The plan no longer matches the live state. The user must review a freshly generated diff.
        CoordinatorDisposition.Invalidated => RouteKeys.ChangeReview,
        CoordinatorDisposition.Canceled => RouteKeys.ChangeReview,

        CoordinatorDisposition.Blocked => RouteKeys.ResultFailure,
        CoordinatorDisposition.BackupFailed => RouteKeys.ResultFailure,
        CoordinatorDisposition.ApplyFailed => RouteKeys.ResultFailure,
        CoordinatorDisposition.VerificationFailed => RouteKeys.ResultFailure,
        CoordinatorDisposition.RollbackFailed => RouteKeys.ResultFailure,

        // Winora cannot prove the current state is safe to continue from. Never resumed silently.
        CoordinatorDisposition.PartialRecoveryRequired => RouteKeys.Recovery,
        CoordinatorDisposition.Conflict => RouteKeys.Recovery,
        CoordinatorDisposition.DurabilityFailure => RouteKeys.Recovery,

        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unmapped coordinator disposition."),
    };
}
