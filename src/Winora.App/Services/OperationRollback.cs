using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.App.ViewModels;
using Winora.Infrastructure.Recovery;

namespace Winora.App.Services;

/// <param name="Succeeded">True when the operation is back to its recorded source state.</param>
/// <param name="FailureResourceKey">Why it did not, as a resource key, or empty on success.</param>
public readonly record struct RollbackAttempt(bool Succeeded, string FailureResourceKey);

/// <summary>Undoes one recorded operation, whether it finished or was left unfinished.</summary>
public interface IOperationRollback
{
    Task<RollbackAttempt> RollBackAsync(Guid operationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The single path back from an applied change.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for both callers on purpose. Recovery undoes an operation that never finished
/// and the history screen undoes one that did, but the sequence is identical — read the verified
/// boundary, load the archived plan, verify the backup, build a rollback plan, hand it to the
/// coordinator. Two copies of that would drift, and the half that drifted would be the one nobody
/// exercised until a user needed it.
/// </para>
/// <para>
/// Nothing here decides whether a rollback is safe; the coordinator does. This assembles the inputs
/// and translates the outcome into something the screen can say.
/// </para>
/// </remarks>
public sealed class OperationRollback : IOperationRollback
{
    private readonly IDurableOperationJournal _journal;
    private readonly IChangePlanArchive _planArchive;
    private readonly IBackupRepository _backups;
    private readonly IOperationCatalog _catalog;
    private readonly ChangeCoordinator _coordinator;
    private readonly ConfirmationAuthority _confirmation;
    private readonly IActionJournalWriter _actionJournal;

    public OperationRollback(
        IDurableOperationJournal journal,
        IChangePlanArchive planArchive,
        IBackupRepository backups,
        IOperationCatalog catalog,
        ChangeCoordinator coordinator,
        ConfirmationAuthority confirmation,
        IActionJournalWriter actionJournal)
    {
        _actionJournal = actionJournal ?? throw new ArgumentNullException(nameof(actionJournal));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _planArchive = planArchive ?? throw new ArgumentNullException(nameof(planArchive));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
    }

    public async Task<RollbackAttempt> RollBackAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        DurableOperationBoundary? boundary;
        try
        {
            boundary = await _journal
                .ReadVerifiedBoundaryAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed("Recovery_JournalUnreadable");
        }

        if (boundary is null)
        {
            return Failed("Recovery_JournalUnreadable");
        }

        var plan = await _planArchive
            .TryLoadAsync(operationId, cancellationToken)
            .ConfigureAwait(false);

        if (plan is null)
        {
            // Operations planned before the archive existed cannot be rolled back by Winora.
            // Reconstructing the plan would change its digest and the coordinator would refuse it
            // as drift anyway, so saying so is the only honest answer.
            return Failed("Recovery_PlanMissing");
        }

        if (boundary.Facts.BackupId is not { } backupId ||
            boundary.Facts.BackupDigest is not { } backupDigest)
        {
            return Failed("Recovery_BackupMissing");
        }

        try
        {
            var receipt = await _backups
                .ReadAndVerifyOperationBackupAsync(plan, backupId, backupDigest, cancellationToken)
                .ConfigureAwait(false);

            // The state the operation would have reached had it finished. Steps that never applied
            // are observed in their source state and reported as AlreadyRestored, so this holds
            // whether the apply got all the way, part way, or nowhere.
            var appliedFingerprint = plan.Steps[^1].ResultFingerprint;

            var rollbackPlan = RollbackPlan.Create(
                Guid.NewGuid(),
                plan,
                receipt,
                appliedFingerprint);

            var operation = _catalog.Resolve(plan.OperationId);
            var result = await _coordinator
                .RollbackAsync(
                    operation,
                    rollbackPlan,
                    _confirmation.Confirm(rollbackPlan),
                    cancellationToken)
                .ConfigureAwait(false);

            var succeeded = result.Disposition is CoordinatorDisposition.RolledBack or
                CoordinatorDisposition.AlreadyRestored;

            // Recorded after the fact and never allowed to change it, for the same reason as apply.
            await _actionJournal
                .RecordRollbackAsync(plan, succeeded, cancellationToken)
                .ConfigureAwait(false);

            return succeeded
                ? new RollbackAttempt(true, string.Empty)
                : Failed(CoordinatorDispositionPresentation.ResourceKeyFor(result.Disposition));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed("Recovery_Failed");
        }
    }

    private static RollbackAttempt Failed(string resourceKey) => new(false, resourceKey);
}
