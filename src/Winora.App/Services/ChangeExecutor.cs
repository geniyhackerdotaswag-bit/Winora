using Winora.App.ViewModels;
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
    private readonly ILocalizationService _text;

    public ChangeExecutor(
        ChangeCoordinator coordinator,
        ConfirmationAuthority confirmation,
        IDeploymentState deployment,
        ILocalizationService text)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
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

        // A fresh token per attempt: the authority consumes it, so a retry cannot replay one.
        var token = _confirmation.Confirm(plan);

        // Lease acquisition and journal writes are synchronous file work; keep them off the UI thread.
        var result = await Task.Run(
            () => _coordinator.ApplyAsync(operation, plan, token, cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(true);

        return new ChangeOutcome(
            result.Disposition == CoordinatorDisposition.Completed,
            _text.Get(CoordinatorDispositionPresentation.ResourceKeyFor(result.Disposition)),
            result.Disposition);
    }
}
