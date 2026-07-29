using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Core.Journal;

namespace Winora.Infrastructure.Journal;

public sealed record ActionJournalIndex(
    DateTimeOffset RebuiltAtUtc,
    IReadOnlyList<ActionJournalEntry> Events);

internal static class ActionJournalSchema
{
    internal static void ValidateDraft(ActionJournalEntryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateGuid(draft.OperationId, nameof(draft.OperationId));
        if (!ChangePlan.IsSafeCatalogOperationId(draft.CatalogOperationId))
        {
            throw new ArgumentException(
                "The stable catalog operation identifier is invalid.",
                nameof(draft.CatalogOperationId));
        }
        ValidateEnum(draft.Kind, nameof(draft.Kind));
        ValidateEnum(draft.Category, nameof(draft.Category));
        ValidateEnum(draft.Status, nameof(draft.Status));
        ValidateEnum(draft.Risk, nameof(draft.Risk));
        ValidateEnum(draft.Privilege, nameof(draft.Privilege));
        ValidateEnum(draft.SupportStatus, nameof(draft.SupportStatus));
        ValidateGuid(draft.CorrelationId, nameof(draft.CorrelationId));
        ValidateTargetCorrelationHash(draft.TargetCorrelationHash);
        if (draft.AffectedItemCount is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draft.AffectedItemCount),
                "The affected-item count cannot be negative.");
        }

        var retentionStatus = IsRetentionStatus(draft.Status);
        if (draft.Kind == ActionJournalEventKind.RetentionDecision)
        {
            if (draft.Category != ActionJournalCategory.Retention || !retentionStatus)
            {
                throw new ArgumentException(
                    "Retention decisions require the retention category and a retention status.",
                    nameof(draft));
            }
        }
        else if (retentionStatus)
        {
            throw new ArgumentException(
                "Retention statuses are reserved for retention-decision events.",
                nameof(draft));
        }
    }

    internal static void ValidateDraft(
        ActionJournalEntryDraft draft,
        IActionJournalOperationCatalog operationCatalog)
    {
        ValidateDraft(draft);
        ArgumentNullException.ThrowIfNull(operationCatalog);
        if (!operationCatalog.IsAllowlisted(draft.CatalogOperationId))
        {
            throw new ArgumentException(
                "The stable catalog operation identifier is not allowlisted.",
                nameof(draft.CatalogOperationId));
        }
    }

    internal static void ValidatePersisted(ActionJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            ValidateEventId(entry.EventId, nameof(entry.EventId));
            ValidateTimestamp(entry.TimestampUtc, nameof(entry.TimestampUtc));
            ValidateDraft(new ActionJournalEntryDraft(
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
                entry.AffectedItemCount));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The persisted action-journal event violates the schema allowlist.",
                exception);
        }
    }

    internal static void ValidateEventId(string eventId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId, parameterName);
        if (!Guid.TryParseExact(eventId, "N", out var parsed) ||
            !StringComparer.Ordinal.Equals(parsed.ToString("N"), eventId))
        {
            throw new ArgumentException(
                "The event identifier must be a canonical lowercase GUID.",
                parameterName);
        }
    }

    internal static void ValidateTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The action-journal timestamp must be a non-default UTC value.",
                parameterName);
        }
    }

    private static bool IsRetentionStatus(ActionJournalStatus status) =>
        status is
            ActionJournalStatus.RetentionApproved or
            ActionJournalStatus.RetentionCompleted or
            ActionJournalStatus.RetentionFailed or
            ActionJournalStatus.RetentionSkipped;

    private static void ValidateGuid(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }

    private static void ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The value is not part of the action-journal schema allowlist.");
        }
    }

    private static void ValidateTargetCorrelationHash(string? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length != 64 ||
            value.Any(character =>
                !char.IsAsciiHexDigit(character) ||
                char.IsAsciiLetterLower(character)))
        {
            throw new ArgumentException(
                "The target correlation value must be an uppercase SHA-256 hash.",
                nameof(value));
        }
    }
}
