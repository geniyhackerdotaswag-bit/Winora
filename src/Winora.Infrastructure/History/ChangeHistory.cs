using Winora.Core.Changes;
using Winora.Infrastructure.Operations;
using Winora.Infrastructure.Recovery;

namespace Winora.Infrastructure.History;

/// <param name="OperationId">Identifies the operation, and is what a rollback is asked for by.</param>
/// <param name="Title">The plan's title, or empty when the plan is no longer on disk.</param>
/// <param name="Summary">What the change did, as "from → to" for its first step.</param>
/// <param name="State">The durable state the operation ended in.</param>
/// <param name="OccurredAtUtc">When the last durable transition was written.</param>
/// <param name="IsComplete">
/// False while the operation is still mid-flight. One of these blocks every new change until it is
/// reconciled, which is why they get a screen of their own.
/// </param>
/// <param name="IsReversible">
/// True when everything a rollback needs is present: a terminal state that actually changed
/// something, the archived plan, and a verified backup binding.
/// </param>
public sealed record ChangeHistoryEntry(
    Guid OperationId,
    string Title,
    string Summary,
    OperationState State,
    DateTimeOffset OccurredAtUtc,
    bool IsComplete,
    bool IsReversible);

/// <summary>Everything Winora has done, newest first.</summary>
public interface IChangeHistory
{
    Task<IReadOnlyList<ChangeHistoryEntry>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the durable journal and the plan archive into something a screen can list.
/// </summary>
/// <remarks>
/// <para>
/// The journal is the authority for what happened and the archive for what was intended; neither
/// alone is enough. The journal holds hash-verified state but not the human title, and the archive
/// holds the canonical plan but says nothing about whether it was applied.
/// </para>
/// <para>
/// An operation whose plan is missing is still listed, with an empty title, rather than hidden. A
/// history that quietly omits entries it cannot fully describe is worse than one that admits the
/// gap — the whole point of the screen is that the user can see everything the app did.
/// </para>
/// </remarks>
public sealed class ChangeHistory : IChangeHistory
{
    private readonly DurableOperationJournal _journal;
    private readonly IChangePlanArchive _archive;

    public ChangeHistory(DurableOperationJournal journal, IChangePlanArchive archive)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    }

    public async Task<IReadOnlyList<ChangeHistoryEntry>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var catalog = await _journal.ScanStorageCatalogAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<ChangeHistoryEntry>(catalog.Count);

        foreach (var record in catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plan = await _archive
                .TryLoadAsync(record.OperationId, cancellationToken)
                .ConfigureAwait(false);

            entries.Add(new ChangeHistoryEntry(
                record.OperationId,
                plan?.Title ?? string.Empty,
                SummaryOf(plan),
                record.State,
                record.TerminalOccurredAtUtc,
                record.IsTerminal,
                IsReversible(record, plan)));
        }

        // Newest first: the change a user wants to undo is almost always the last one they made.
        return entries.OrderByDescending(static entry => entry.OccurredAtUtc).ToArray();
    }

    /// <remarks>
    /// Deliberately strict. Offering a rollback that then fails for a missing backup would break the
    /// one promise this screen exists to keep, so a change is only advertised as reversible when
    /// every piece is already on disk.
    /// </remarks>
    private static bool IsReversible(OperationStorageCatalogEntry record, ChangePlan? plan) =>
        plan is not null &&
        record.IsTerminal &&
        record.BackupId is not null &&
        record.BackupDigest is not null &&
        // Only a state that actually wrote something can be undone; a plan that was cancelled or
        // blocked never touched Windows, so "roll back" would be a button that does nothing.
        record.State is OperationState.Completed or OperationState.VerificationFailedRollbackOffered;

    private static string SummaryOf(ChangePlan? plan)
    {
        if (plan is null || plan.Steps.Count == 0)
        {
            return string.Empty;
        }

        var step = plan.Steps[0];
        return $"{step.CurrentValue.Text} → {step.ProposedValue.Text}";
    }
}
