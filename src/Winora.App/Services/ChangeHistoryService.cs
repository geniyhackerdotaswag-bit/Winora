using Winora.Core.Changes;
using Winora.Infrastructure.History;

namespace Winora.App.Services;

/// <param name="OperationId">Identity, and what a rollback is asked for by.</param>
/// <param name="Title">What was changed.</param>
/// <param name="Summary">The change itself, as "from → to".</param>
/// <param name="OutcomeResourceKey">Resource key describing how the operation ended.</param>
/// <param name="OccurredAtUtc">When the last durable transition was written.</param>
/// <param name="IsComplete">False while the operation is still mid-flight and blocking new ones.</param>
/// <param name="IsReversible">True when Winora can still undo it.</param>
public sealed record ChangeRecordView(
    Guid OperationId,
    string Title,
    string Summary,
    string OutcomeResourceKey,
    DateTimeOffset OccurredAtUtc,
    bool IsComplete,
    bool IsReversible);

/// <summary>What Winora has done, and the way to undo any of it.</summary>
public interface IChangeHistoryService
{
    Task<IReadOnlyList<ChangeRecordView>> ReadAsync(CancellationToken cancellationToken = default);

    Task<RollbackAttempt> RollBackAsync(Guid operationId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ChangeHistoryService : IChangeHistoryService
{
    private readonly IChangeHistory _history;
    private readonly IOperationRollback _rollback;

    public ChangeHistoryService(IChangeHistory history, IOperationRollback rollback)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
    }

    public async Task<IReadOnlyList<ChangeRecordView>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await _history.ReadAsync(cancellationToken).ConfigureAwait(false);

        return entries
            .Select(static entry => new ChangeRecordView(
                entry.OperationId,
                entry.Title,
                entry.Summary,
                OutcomeKeyFor(entry.State),
                entry.OccurredAtUtc,
                entry.IsComplete,
                entry.IsReversible))
            .ToArray();
    }

    public Task<RollbackAttempt> RollBackAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        _rollback.RollBackAsync(operationId, cancellationToken);

    /// <remarks>
    /// Grouped rather than one string per state. There are forty-odd durable states and a user does
    /// not need to know which internal step a change stopped at — they need to know whether it took
    /// effect, whether it was undone, and whether something still needs attention. States that mean
    /// the same thing to a person therefore share a message.
    /// </remarks>
    private static string OutcomeKeyFor(OperationState state) => state switch
    {
        OperationState.Completed => "Changes_Outcome_Applied",
        OperationState.RolledBack or OperationState.AlreadyRestored => "Changes_Outcome_RolledBack",
        OperationState.Unsupported => "Changes_Outcome_Unsupported",
        OperationState.CanceledNoChanges or
            OperationState.ElevationCanceledNoChanges or
            OperationState.PlanInvalidatedNoChanges => "Changes_Outcome_Cancelled",
        OperationState.BackupFailedNoChanges or
            OperationState.RestorePointFailedNoChanges or
            OperationState.ApplyFailedNoChanges or
            OperationState.ApplyStepNotApplied => "Changes_Outcome_FailedNoChanges",
        OperationState.VerificationFailedRollbackOffered => "Changes_Outcome_VerificationFailed",
        OperationState.PartiallyAppliedRecoveryRequired or
            OperationState.RollbackFailedRecoveryRequired or
            OperationState.RecoveryConflictExternalDrift => "Changes_Outcome_NeedsRecovery",
        _ => "Changes_Outcome_InProgress",
    };
}
