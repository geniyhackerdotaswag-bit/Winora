using Winora.Core.Contracts;
using Winora.Core.Journal;

namespace Winora.App.Services;

/// <param name="TimestampUtc">When the entry was written.</param>
/// <param name="CategoryResourceKey">Which part of Windows it concerned.</param>
/// <param name="StatusResourceKey">How it ended.</param>
/// <param name="RiskResourceKey">The risk the plan carried.</param>
/// <param name="NeededAdministrator">True when the change required administrator rights.</param>
public sealed record ActionRecordView(
    DateTimeOffset TimestampUtc,
    string CategoryResourceKey,
    string StatusResourceKey,
    string RiskResourceKey,
    bool NeededAdministrator);

/// <summary>The sanitized audit trail, for the presentation layer.</summary>
public interface IActionJournalReader
{
    Task<IReadOnlyList<ActionRecordView>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ActionJournalReader : IActionJournalReader
{
    private readonly IActionJournal _journal;

    public ActionJournalReader(IActionJournal journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public async Task<IReadOnlyList<ActionRecordView>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);

        return entries
            // Newest first, matching every other list in the app.
            .OrderByDescending(static entry => entry.TimestampUtc)
            .Select(static entry => new ActionRecordView(
                entry.TimestampUtc,
                "Journal_Category_" + entry.Category,
                "Journal_Status_" + entry.Status,
                "Journal_Risk_" + entry.Risk,
                entry.Privilege == ActionJournalPrivilege.Administrator))
            .ToArray();
    }
}
