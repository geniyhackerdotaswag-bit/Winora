namespace Winora.Core.Journal;

public enum ActionJournalEventKind
{
    Operation,
    RetentionDecision,
}

public enum ActionJournalCategory
{
    WindowsPersonalization,
    Startup,
    SystemSounds,
    Cursors,
    Icons,
    Backup,
    Recovery,
    Retention,
    Application,
}

public enum ActionJournalStatus
{
    Planned,
    Succeeded,
    Failed,
    RecoveryRequired,
    RolledBack,
    RollbackFailed,
    RetentionApproved,
    RetentionCompleted,
    RetentionFailed,
    RetentionSkipped,
}

public enum ActionJournalRisk
{
    Low,
    Medium,
    High,
}

public enum ActionJournalPrivilege
{
    StandardUser,
    Administrator,
}

public enum ActionJournalSupportStatus
{
    Supported,
    Guided,
    Unsupported,
}

public sealed record ActionJournalEntryDraft(
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
    int? AffectedItemCount);

public sealed record ActionJournalEntry(
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
    int? AffectedItemCount);
