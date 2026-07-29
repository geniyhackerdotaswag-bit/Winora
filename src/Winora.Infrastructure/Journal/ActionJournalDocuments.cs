using Winora.Core.Contracts;
using Winora.Core.Journal;

namespace Winora.Infrastructure.Journal;

internal sealed record ActionJournalDocument(
    string EventId,
    DateTimeOffset TimestampUtc,
    Guid OperationId,
    string CatalogOperationId,
    ActionJournalEventKind Kind,
    ActionJournalCategory Category,
    ActionJournalStatus Status,
    ActionJournalRisk Risk,
    ActionJournalPrivilege Privilege,
    ActionJournalSupportStatus SupportStatus,
    Guid CorrelationId,
    string? TargetCorrelationHash,
    int? AffectedItemCount)
{
    internal static ActionJournalDocument Create(
        string eventId,
        ActionJournalEntryDraft draft,
        DateTimeOffset timestampUtc,
        IActionJournalOperationCatalog operationCatalog)
    {
        ActionJournalSchema.ValidateEventId(eventId, nameof(eventId));
        ActionJournalSchema.ValidateTimestamp(timestampUtc, nameof(timestampUtc));
        ActionJournalSchema.ValidateDraft(draft, operationCatalog);
        return new ActionJournalDocument(
            eventId,
            timestampUtc,
            draft.OperationId,
            draft.CatalogOperationId,
            draft.Kind,
            draft.Category,
            draft.Status,
            draft.Risk,
            draft.Privilege,
            draft.SupportStatus,
            draft.CorrelationId,
            draft.TargetCorrelationHash,
            draft.AffectedItemCount);
    }

    internal ActionJournalEntry Rehydrate(IActionJournalOperationCatalog operationCatalog)
    {
        var entry = new ActionJournalEntry(
            EventId,
            TimestampUtc,
            OperationId,
            CatalogOperationId,
            Kind,
            Category,
            Status,
            Risk,
            Privilege,
            SupportStatus,
            CorrelationId,
            TargetCorrelationHash,
            AffectedItemCount);
        ActionJournalSchema.ValidatePersisted(entry);
        if (!operationCatalog.IsAllowlisted(entry.CatalogOperationId))
        {
            throw new InvalidDataException(
                "The persisted catalog operation identifier is not allowlisted.");
        }

        return entry;
    }
}

internal sealed record ActionJournalIndexDocument(
    DateTimeOffset RebuiltAtUtc,
    IReadOnlyList<ActionJournalDocument> Events)
{
    internal static ActionJournalIndexDocument Create(
        DateTimeOffset rebuiltAtUtc,
        IReadOnlyList<ActionJournalEntry> events)
    {
        ActionJournalSchema.ValidateTimestamp(rebuiltAtUtc, nameof(rebuiltAtUtc));
        ArgumentNullException.ThrowIfNull(events);
        return new ActionJournalIndexDocument(
            rebuiltAtUtc,
            Array.AsReadOnly(events.Select(FromEntry).ToArray()));
    }

    internal ActionJournalIndex Rehydrate(IActionJournalOperationCatalog operationCatalog)
    {
        try
        {
            ActionJournalSchema.ValidateTimestamp(RebuiltAtUtc, nameof(RebuiltAtUtc));
            if (Events is null)
            {
                throw new InvalidDataException("The action-journal index event list is missing.");
            }

            ArgumentNullException.ThrowIfNull(operationCatalog);
            var events = Events.Select(document => document.Rehydrate(operationCatalog)).ToArray();
            if (events.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != events.Length)
            {
                throw new InvalidDataException("The action-journal index contains duplicate events.");
            }

            return new ActionJournalIndex(RebuiltAtUtc, Array.AsReadOnly(events));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The action-journal index is invalid.", exception);
        }
    }

    private static ActionJournalDocument FromEntry(ActionJournalEntry entry)
    {
        ActionJournalSchema.ValidatePersisted(entry);
        return new ActionJournalDocument(
            entry.EventId,
            entry.TimestampUtc,
            entry.OperationId,
            entry.CatalogOperationId,
            entry.Kind,
            entry.Category,
            entry.Status,
            entry.Risk,
            entry.Privilege,
            entry.SupportStatus,
            entry.CorrelationId,
            entry.TargetCorrelationHash,
            entry.AffectedItemCount);
    }
}
