using Winora.App.ViewModels;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Recovery;

namespace Winora.App.Services;

/// <param name="Recovered">Operations reconciled to a terminal state.</param>
/// <param name="Failed">Operations that could not be reconciled.</param>
/// <param name="FirstFailure">Why the first failure happened, or empty.</param>
public readonly record struct RecoveryOutcome(int Recovered, int Failed, string FirstFailure);

/// <summary>
/// Whether an earlier change was left unfinished, and the way out of it.
/// </summary>
/// <remarks>
/// This matters far more than its size suggests. The mutation lease refuses to be granted while any
/// operation is incomplete, by design — an unfinished change must be reconciled before another one
/// starts. But the lease can only answer "no", so every screen reported the refusal as "another
/// operation is already running", which is not what happened and gave the user nothing to act on.
/// Asking the journal directly is what lets the app say the true thing, and roll the stuck
/// operation back so the app is usable again.
/// </remarks>
public interface IRecoveryState
{
    /// <summary>Operations left in a non-terminal state, which block every new change.</summary>
    Task<int> PendingCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls back every incomplete operation, which is the only way out of that state.</summary>
    Task<RecoveryOutcome> RecoverAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class RecoveryState : IRecoveryState
{
    private readonly IDurableOperationJournal _journal;
    private readonly IOperationRollback _rollback;
    private readonly ILocalizationService _text;

    public RecoveryState(
        IDurableOperationJournal journal,
        IOperationRollback rollback,
        ILocalizationService text)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public async Task<int> PendingCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var incomplete = await _journal.ScanIncompleteAsync(cancellationToken).ConfigureAwait(false);
            return incomplete.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A journal Winora cannot read is itself a reason to stop, but reporting it as a pending
            // recovery would be a guess. The caller treats zero as "nothing known to be pending".
            return 0;
        }
    }

    public async Task<RecoveryOutcome> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var recovered = 0;
        var failed = 0;
        var firstFailure = string.Empty;

        void Fail(string reason)
        {
            failed++;
            if (firstFailure.Length == 0)
            {
                firstFailure = reason;
            }
        }

        IReadOnlyList<DurableOperationBoundary> incomplete;
        try
        {
            incomplete = await _journal.ScanIncompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RecoveryOutcome(0, 1, _text.Get("Recovery_JournalUnreadable"));
        }

        foreach (var boundary in incomplete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The same path the history screen uses to undo a change that did finish. One
            // implementation, so the rarely-exercised half cannot drift away from the other.
            var attempt = await _rollback
                .RollBackAsync(boundary.OperationId, cancellationToken)
                .ConfigureAwait(false);

            if (attempt.Succeeded)
            {
                recovered++;
            }
            else
            {
                Fail(_text.Get(attempt.FailureResourceKey));
            }
        }

        return new RecoveryOutcome(recovered, failed, firstFailure);
    }
}
