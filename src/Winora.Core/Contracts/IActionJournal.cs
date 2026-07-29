using Winora.Core.Journal;

namespace Winora.Core.Contracts;

public interface IActionJournal
{
    ValueTask<ActionJournalEntry> AppendAsync(
        ActionJournalEntryDraft draft,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ActionJournalEntry>> ReadAllAsync(
        CancellationToken cancellationToken);
}

public interface IActionJournalOperationCatalog
{
    bool IsAllowlisted(string catalogOperationId);
}
