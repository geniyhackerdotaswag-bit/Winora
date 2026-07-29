using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Operations;

namespace Winora.Infrastructure.Journal;

internal enum RetentionLifecycleState
{
    Approved = 0,
    DeletingOperation = 1,
    OperationDeleted = 2,
    DeletingBackup = 3,
    BackupDeleted = 4,
    DeletingActionEvents = 5,
    ActionEventsDeleted = 6,
    Completed = 7,
}

internal enum RetentionMutationKind
{
    Operation = 0,
    Backup = 1,
    ActionEvents = 2,
}

internal sealed class ActionJournalRetentionRequest
{
    internal static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(365);
    internal const int DefaultMaximumEventCount = 25_000;

    internal ActionJournalRetentionRequest(
        Guid? completedOperationId,
        IReadOnlySet<Guid> linkedChangeOperationIds,
        TimeSpan maximumAge,
        int maximumEventCount)
    {
        if (completedOperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A completed operation identifier cannot be empty.",
                nameof(completedOperationId));
        }

        ArgumentNullException.ThrowIfNull(linkedChangeOperationIds);
        if (linkedChangeOperationIds.Contains(Guid.Empty))
        {
            throw new ArgumentException(
                "Linked operation identifiers cannot be empty.",
                nameof(linkedChangeOperationIds));
        }

        if (maximumAge != DefaultMaximumAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAge),
                "Production retention uses the fixed 365-day journal age policy.");
        }

        if (maximumEventCount != DefaultMaximumEventCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEventCount),
                "Production retention uses the fixed 25,000-event journal cap.");
        }

        CompletedOperationId = completedOperationId;
        LinkedChangeOperationIds = new HashSet<Guid>(linkedChangeOperationIds);
        MaximumAge = maximumAge;
        MaximumEventCount = maximumEventCount;
    }

    internal Guid? CompletedOperationId { get; }

    internal IReadOnlySet<Guid> LinkedChangeOperationIds { get; }

    internal TimeSpan MaximumAge { get; }

    internal int MaximumEventCount { get; }
}

internal sealed record RetentionOperationIdentity(
    Guid OperationId,
    long Revision,
    OperationState State,
    string LastEventHash,
    DateTimeOffset TerminalOccurredAtUtc,
    string PlanDigest,
    string? BackupId,
    string? BackupDigest,
    uint RootVolumeSerialNumber,
    ulong RootFileIndex,
    bool IsTerminal,
    bool IsRecoveryProtected);

internal sealed record RetentionBackupIdentity(
    string BackupId,
    BackupStorageStatus Status,
    BackupCaptureKind Kind,
    string PlanDigest,
    string BackupDigest,
    BackupProtectionClass Protection,
    bool IsVerified,
    bool IsRecoveryProtected,
    DateTimeOffset CommittedUtc);

internal sealed record RetentionActionEventIdentity(
    string EventId,
    string PayloadSha256,
    uint VolumeSerialNumber,
    ulong FileIndex);

internal sealed record RetentionLinkedStateSnapshot(
    int SchemaVersion,
    Guid? ExcludedCompletedOperationId,
    string CatalogSha256,
    IReadOnlyList<Guid> LinkedOperationIds,
    string SnapshotSha256)
{
    internal const int CurrentSchemaVersion = 1;

    internal static RetentionLinkedStateSnapshot Create(
        IReadOnlyList<OperationStorageCatalogEntry> catalog,
        Guid? excludedCompletedOperationId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (excludedCompletedOperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An excluded completed operation identifier cannot be empty.",
                nameof(excludedCompletedOperationId));
        }

        if (catalog.Any(item => item is null) ||
            catalog.Select(item => item.OperationId).Distinct().Count() != catalog.Count)
        {
            throw new InvalidDataException(
                "The authoritative linked-state catalog contains duplicate or null operations.");
        }

        var linkedCatalog = catalog
            .Where(item => item.OperationId != excludedCompletedOperationId)
            .OrderBy(item => item.OperationId)
            .ToArray();
        var linkedIds = Array.AsReadOnly(linkedCatalog
            .Select(item => item.OperationId)
            .ToArray());
        var catalogSha256 = ComputeCatalogSha256(linkedCatalog);
        return new RetentionLinkedStateSnapshot(
            CurrentSchemaVersion,
            excludedCompletedOperationId,
            catalogSha256,
            linkedIds,
            ComputeSnapshotSha256(
                CurrentSchemaVersion,
                excludedCompletedOperationId,
                catalogSha256,
                linkedIds));
    }

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "The authoritative linked-state snapshot schema version is unsupported.");
        }

        if (ExcludedCompletedOperationId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The authoritative linked-state excluded operation identifier is empty.");
        }

        if (!IsUpperSha256(CatalogSha256))
        {
            throw new InvalidDataException(
                "The authoritative linked-state catalog digest is invalid.");
        }

        if (LinkedOperationIds is null ||
            LinkedOperationIds.Contains(Guid.Empty) ||
            LinkedOperationIds.Distinct().Count() != LinkedOperationIds.Count ||
            !LinkedOperationIds.SequenceEqual(LinkedOperationIds.Order()) ||
            (ExcludedCompletedOperationId is { } excluded && LinkedOperationIds.Contains(excluded)))
        {
            throw new InvalidDataException(
                "The authoritative linked-state operation identifiers are invalid.");
        }

        if (!IsUpperSha256(SnapshotSha256) ||
            !StringComparer.Ordinal.Equals(
                SnapshotSha256,
                ComputeSnapshotSha256(
                    SchemaVersion,
                    ExcludedCompletedOperationId,
                    CatalogSha256,
                    LinkedOperationIds)))
        {
            throw new InvalidDataException(
                "The authoritative linked-state snapshot digest is invalid.");
        }
    }

    internal bool MatchesCatalog(IReadOnlyList<OperationStorageCatalogEntry> catalog)
    {
        var current = Create(catalog, ExcludedCompletedOperationId);
        return SchemaVersion == current.SchemaVersion &&
               ExcludedCompletedOperationId == current.ExcludedCompletedOperationId &&
               StringComparer.Ordinal.Equals(CatalogSha256, current.CatalogSha256) &&
               LinkedOperationIds.SequenceEqual(current.LinkedOperationIds) &&
               StringComparer.Ordinal.Equals(SnapshotSha256, current.SnapshotSha256);
    }

    private static string ComputeCatalogSha256(
        IReadOnlyList<OperationStorageCatalogEntry> catalog)
    {
        var canonical = new StringBuilder();
        Append(canonical, CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        foreach (var item in catalog)
        {
            Append(canonical, item.OperationId.ToString("N"));
            Append(canonical, item.Revision.ToString(CultureInfo.InvariantCulture));
            Append(canonical, ((int)item.State).ToString(CultureInfo.InvariantCulture));
            Append(canonical, item.LastEventHash);
            Append(canonical, item.TerminalOccurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
            Append(canonical, item.PlanDigest);
            Append(canonical, item.BackupId);
            Append(canonical, item.BackupDigest);
            Append(canonical, item.RootVolumeSerialNumber.ToString(CultureInfo.InvariantCulture));
            Append(canonical, item.RootFileIndex.ToString(CultureInfo.InvariantCulture));
            Append(canonical, item.IsTerminal ? "1" : "0");
            Append(canonical, item.IsRecoveryProtected ? "1" : "0");
        }

        return Hash(canonical.ToString());
    }

    private static string ComputeSnapshotSha256(
        int schemaVersion,
        Guid? excludedCompletedOperationId,
        string catalogSha256,
        IReadOnlyList<Guid> linkedOperationIds)
    {
        var canonical = new StringBuilder();
        Append(canonical, schemaVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, excludedCompletedOperationId?.ToString("N"));
        Append(canonical, catalogSha256);
        foreach (var operationId in linkedOperationIds)
        {
            Append(canonical, operationId.ToString("N"));
        }

        return Hash(canonical.ToString());
    }

    private static void Append(StringBuilder canonical, string? value)
    {
        value ??= string.Empty;
        _ = canonical
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsUpperSha256(string value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(character =>
            char.IsAsciiHexDigit(character) && !char.IsAsciiLetterLower(character));
}

internal sealed record RetentionArtifactSelection(
    RetentionOperationIdentity? Operation,
    RetentionBackupIdentity? Backup,
    IReadOnlyList<RetentionActionEventIdentity> ActionEvents,
    RetentionLinkedStateSnapshot LinkedState)
{
    internal static RetentionArtifactSelection Empty { get; } = new(
        null,
        null,
        [],
        RetentionLinkedStateSnapshot.Create([], excludedCompletedOperationId: null));
}

internal sealed record RetentionIntentDocument(
    int SchemaVersion,
    Guid TransactionId,
    DateTimeOffset ApprovedUtc,
    Guid ApprovedLeaseId,
    long ApprovedLeaseEpoch,
    Guid? CompletedOperationId,
    int LinkedStateSchemaVersion,
    string LinkedCatalogSha256,
    IReadOnlyList<Guid> LinkedChangeOperationIds,
    string LinkedStateSha256,
    long MaximumAgeTicks,
    int MaximumEventCount,
    RetentionOperationIdentity? Operation,
    RetentionBackupIdentity? Backup,
    IReadOnlyList<RetentionActionEventIdentity> ActionEvents)
{
    internal const int CurrentSchemaVersion = 2;

    internal static RetentionIntentDocument Create(
        Guid transactionId,
        DateTimeOffset approvedUtc,
        IMutationLeaseHandle lease,
        ActionJournalRetentionRequest request,
        RetentionArtifactSelection selection)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selection);
        selection.LinkedState.Validate();
        if (selection.LinkedState.ExcludedCompletedOperationId != request.CompletedOperationId ||
            !selection.LinkedState.LinkedOperationIds.ToHashSet()
                .SetEquals(request.LinkedChangeOperationIds))
        {
            throw new InvalidOperationException(
                "The authoritative linked-state snapshot does not match the retention request expectation.");
        }

        var document = new RetentionIntentDocument(
            CurrentSchemaVersion,
            transactionId,
            approvedUtc,
            lease.LeaseId,
            lease.Epoch,
            request.CompletedOperationId,
            selection.LinkedState.SchemaVersion,
            selection.LinkedState.CatalogSha256,
            Array.AsReadOnly(selection.LinkedState.LinkedOperationIds.ToArray()),
            selection.LinkedState.SnapshotSha256,
            request.MaximumAge.Ticks,
            request.MaximumEventCount,
            selection.Operation,
            selection.Backup,
            Array.AsReadOnly(selection.ActionEvents
                .OrderBy(item => item.EventId, StringComparer.Ordinal)
                .ToArray()));
        RetentionMaintenanceSchema.Validate(document);
        return document;
    }

    internal RetentionLinkedStateSnapshot RehydrateLinkedState() =>
        new(
            LinkedStateSchemaVersion,
            CompletedOperationId,
            LinkedCatalogSha256,
            LinkedChangeOperationIds,
            LinkedStateSha256);
}

internal sealed record RetentionStateDocument(
    int SchemaVersion,
    Guid TransactionId,
    RetentionLifecycleState State,
    long Revision,
    Guid LeaseId,
    long LeaseEpoch,
    DateTimeOffset UpdatedUtc)
{
    internal const int CurrentSchemaVersion = 1;
}

internal sealed record RetentionTransactionBoundary(
    RetentionIntentDocument Intent,
    RetentionLifecycleState State,
    long Revision,
    Guid LeaseId,
    long LeaseEpoch,
    DateTimeOffset UpdatedUtc);

internal sealed record RetentionMaintenanceResult(
    Guid TransactionId,
    RetentionLifecycleState State,
    bool Resumed,
    int PlannedActionEventDeletes);

internal interface IRetentionArtifactStore
{
    ValueTask<RetentionArtifactSelection> CaptureAsync(
        ActionJournalRetentionRequest request,
        DateTimeOffset nowUtc,
        int reservedDecisionEventCount,
        CancellationToken cancellationToken);

    ValueTask VerifyLinkedStateAsync(
        RetentionLinkedStateSnapshot expected,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteOperationAsync(
        RetentionTransactionBoundary boundary,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteBackupAsync(
        RetentionTransactionBoundary boundary,
        CancellationToken cancellationToken);

    ValueTask<int> DeleteActionEventsAsync(
        RetentionTransactionBoundary boundary,
        IMutationLeaseHandle lease,
        CancellationToken cancellationToken);

    ValueTask RebuildActionIndexAsync(CancellationToken cancellationToken);
}

internal interface IRetentionMaintenanceFaultInjector
{
    void AfterMutation(RetentionMutationKind kind);
}

internal sealed class RetentionLeaseLostException : InvalidOperationException
{
    internal RetentionLeaseLostException()
        : base("The global mutation lease changed immediately before retention mutation.")
    {
    }
}

internal static class RetentionMaintenanceSchema
{
    internal static void Validate(RetentionIntentDocument intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.SchemaVersion != RetentionIntentDocument.CurrentSchemaVersion ||
            intent.TransactionId == Guid.Empty ||
            !IsUtc(intent.ApprovedUtc) ||
            intent.ApprovedLeaseId == Guid.Empty ||
            intent.ApprovedLeaseEpoch <= 0 ||
            intent.CompletedOperationId == Guid.Empty ||
            intent.MaximumAgeTicks != ActionJournalRetentionRequest.DefaultMaximumAge.Ticks ||
            intent.MaximumEventCount != ActionJournalRetentionRequest.DefaultMaximumEventCount ||
            intent.ActionEvents is null ||
            intent.ActionEvents.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() !=
                intent.ActionEvents.Count ||
            !intent.ActionEvents.SequenceEqual(
                intent.ActionEvents.OrderBy(item => item.EventId, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The durable retention intent is invalid.");
        }


        new RetentionLinkedStateSnapshot(
            intent.LinkedStateSchemaVersion,
            intent.CompletedOperationId,
            intent.LinkedCatalogSha256,
            intent.LinkedChangeOperationIds,
            intent.LinkedStateSha256).Validate();

        ValidateOperation(intent.Operation);
        ValidateBackup(intent.Backup);
        foreach (var actionEvent in intent.ActionEvents)
        {
            ValidateActionEvent(actionEvent);
        }

        if ((intent.CompletedOperationId is null) != (intent.Operation is null) ||
            (intent.CompletedOperationId is { } completedOperationId &&
             intent.Operation is { } selectedOperation &&
             completedOperationId != selectedOperation.OperationId))
        {
            throw new InvalidDataException(
                "The completed operation identifier does not match the selected operation artifact.");
        }

        if (intent.Operation is { } linkedOperation &&
            intent.LinkedChangeOperationIds.Contains(linkedOperation.OperationId))
        {
            throw new InvalidDataException(
                "The selected operation artifact remains linked by the authoritative snapshot.");
        }

        if ((intent.Operation?.BackupId is null) != (intent.Backup is null) ||
            (intent.Operation?.BackupDigest is null) != (intent.Backup is null))
        {
            throw new InvalidDataException(
                "The retention operation and backup identities must have an exact binding.");
        }

        if (intent.Operation is { BackupId: { } backupId, BackupDigest: { } backupDigest } &&
            intent.Backup is { } backup &&
            (!StringComparer.Ordinal.Equals(backupId, backup.BackupId) ||
             !StringComparer.Ordinal.Equals(backupDigest, backup.BackupDigest) ||
             !StringComparer.Ordinal.Equals(intent.Operation.PlanDigest, backup.PlanDigest)))
        {
            throw new InvalidDataException(
                "The retained backup does not match the durable operation binding.");
        }
    }

    internal static void Validate(
        RetentionStateDocument state,
        RetentionIntentDocument intent)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(intent);
        if (state.SchemaVersion != RetentionStateDocument.CurrentSchemaVersion ||
            state.TransactionId != intent.TransactionId ||
            !Enum.IsDefined(state.State) ||
            state.Revision <= 0 ||
            state.LeaseId == Guid.Empty ||
            state.LeaseEpoch <= 0 ||
            state.LeaseEpoch < intent.ApprovedLeaseEpoch ||
            !IsUtc(state.UpdatedUtc) ||
            state.UpdatedUtc < intent.ApprovedUtc)
        {
            throw new InvalidDataException("The durable retention lifecycle state is invalid.");
        }

        if (state.State == RetentionLifecycleState.DeletingOperation &&
            intent.Operation is null)
        {
            throw new InvalidDataException(
                "The deleting-operation lifecycle state requires an operation artifact.");
        }

        if (state.State == RetentionLifecycleState.DeletingBackup &&
            intent.Backup is null)
        {
            throw new InvalidDataException(
                "The deleting-backup lifecycle state requires a backup artifact.");
        }

        if (state.State == RetentionLifecycleState.DeletingActionEvents &&
            intent.ActionEvents.Count == 0)
        {
            throw new InvalidDataException(
                "The deleting-action-events lifecycle state requires at least one event artifact.");
        }
    }

    private static void ValidateOperation(RetentionOperationIdentity? operation)
    {
        if (operation is null)
        {
            return;
        }

        if (operation.OperationId == Guid.Empty ||
            operation.Revision <= 0 ||
            !Enum.IsDefined(operation.State) ||
            !operation.IsTerminal ||
            operation.IsRecoveryProtected ||
            !IsUpperSha256(operation.LastEventHash) ||
            !IsUtc(operation.TerminalOccurredAtUtc) ||
            string.IsNullOrWhiteSpace(operation.PlanDigest) ||
            operation.RootFileIndex == 0 ||
            ((operation.BackupId is null) != (operation.BackupDigest is null)) ||
            (operation.BackupId is { } backupId && !IsCanonicalGuid(backupId)) ||
            (operation.BackupDigest is { } backupDigest && !IsUpperSha256(backupDigest)))
        {
            throw new InvalidDataException("The exact retained operation identity is invalid.");
        }
    }

    private static void ValidateBackup(RetentionBackupIdentity? backup)
    {
        if (backup is null)
        {
            return;
        }

        if (!IsCanonicalGuid(backup.BackupId) ||
            backup.Status != BackupStorageStatus.VerifiedCommitted ||
            backup.Kind != BackupCaptureKind.Operation ||
            string.IsNullOrWhiteSpace(backup.PlanDigest) ||
            !IsUpperSha256(backup.BackupDigest) ||
            backup.Protection != BackupProtectionClass.OperationRollbackSource ||
            !backup.IsVerified ||
            !IsUtc(backup.CommittedUtc))
        {
            throw new InvalidDataException("The exact retained backup identity is invalid.");
        }
    }

    private static void ValidateActionEvent(RetentionActionEventIdentity actionEvent)
    {
        ArgumentNullException.ThrowIfNull(actionEvent);
        if (!IsCanonicalGuid(actionEvent.EventId) ||
            !IsUpperSha256(actionEvent.PayloadSha256))
        {
            throw new InvalidDataException("The exact retained action-event identity is invalid.");
        }
    }

    private static bool IsUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    private static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "N", out var parsed) &&
        StringComparer.Ordinal.Equals(value, parsed.ToString("N"));

    private static bool IsUpperSha256(string value) =>
        value.Length == 64 &&
        value.All(character =>
            char.IsAsciiHexDigit(character) && !char.IsAsciiLetterLower(character));
}
