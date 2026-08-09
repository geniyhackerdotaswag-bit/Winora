using Winora.App.ViewModels;
using Winora.Infrastructure.Recovery;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.App.Services;

/// <param name="Succeeded">True when the change applied and verified.</param>
/// <param name="Message">Localized text describing the outcome.</param>
/// <param name="Disposition">The coordinator's own verdict, for callers that need to branch.</param>
public sealed record ChangeOutcome(bool Succeeded, string Message, CoordinatorDisposition Disposition);

/// <summary>
/// Runs one change from draft to verified result in a single call.
/// </summary>
/// <remarks>
/// The user's action on a switch is the confirmation. There is no separate review screen for these
/// changes, but nothing under it was removed: the plan is still built and its digest checked, a
/// verified backup is still taken, the write is still conditional and refuses drift, the result is
/// still verified by an independent read, and every transition is still journaled durably. What
/// changed is where the confirmation comes from, not whether the safety pipeline runs.
/// </remarks>
public interface IChangeExecutor
{
    Task<ChangeOutcome> ApplyAsync(IOperation operation, OperationDraft draft, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ChangeExecutor : IChangeExecutor
{
    private readonly ChangeCoordinator _coordinator;
    private readonly ConfirmationAuthority _confirmation;
    private readonly IDeploymentState _deployment;
    private readonly IRecoveryState _recovery;
    private readonly IChangePlanArchive _planArchive;
    private readonly IActionJournalWriter _actionJournal;
    private readonly ILocalizationService _text;

    public ChangeExecutor(
        ChangeCoordinator coordinator,
        ConfirmationAuthority confirmation,
        IDeploymentState deployment,
        IRecoveryState recovery,
        IChangePlanArchive planArchive,
        IActionJournalWriter actionJournal,
        ILocalizationService text)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _planArchive = planArchive ?? throw new ArgumentNullException(nameof(planArchive));
        _actionJournal = actionJournal ?? throw new ArgumentNullException(nameof(actionJournal));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public async Task<ChangeOutcome> ApplyAsync(
        IOperation operation,
        OperationDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(draft);

        if (!_deployment.CanApplyChanges)
        {
            return new ChangeOutcome(
                false,
                _text.Get(_deployment.ApplyBlockReasonKey ?? "Result_Blocked"),
                CoordinatorDisposition.Blocked);
        }

        // Asked before planning, because the lease will refuse anyway and its refusal cannot say
        // why. Without this the user is told another operation is running when in fact an earlier
        // one never finished and nothing can proceed until it is reconciled.
        if (await _recovery.PendingCountAsync(cancellationToken).ConfigureAwait(true) > 0)
        {
            return new ChangeOutcome(
                false,
                _text.Get("Result_RecoveryPending"),
                CoordinatorDisposition.PartialRecoveryRequired);
        }

        ChangePlan plan;
        try
        {
            plan = await operation.PreviewAsync(draft, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The operation refused to plan: the target is unreadable, or already holds the value.
            return new ChangeOutcome(false, _text.Get("Result_Blocked"), CoordinatorDisposition.Blocked);
        }

        // Archived before anything is written. If the apply stops halfway, this is the only copy of
        // the plan that can drive a rollback, and a plan saved afterwards would be too late.
        await _planArchive.SaveAsync(plan, cancellationToken).ConfigureAwait(true);

        // A fresh token per attempt: the authority consumes it, so a retry cannot replay one.
        var token = _confirmation.Confirm(plan);

        // Lease acquisition and journal writes are synchronous file work; keep them off the UI thread.
        var result = await Task.Run(
            () => _coordinator.ApplyAsync(operation, plan, token, cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(true);

        // After the outcome is known, and never allowed to change it: the change has already
        // happened by this point, so a journal failure must not be reported as a failed change.
        await _actionJournal
            .RecordApplyAsync(plan, result.Disposition, cancellationToken)
            .ConfigureAwait(true);

        return new ChangeOutcome(
            result.Disposition == CoordinatorDisposition.Completed,
            _text.Get(CoordinatorDispositionPresentation.ResourceKeyFor(result.Disposition)),
            result.Disposition);
    }
}
