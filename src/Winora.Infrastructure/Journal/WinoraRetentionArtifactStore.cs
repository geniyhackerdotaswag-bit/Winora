using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Core.Journal;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Operations;

namespace Winora.Infrastructure.Journal;

internal interface IOperationRetentionStore
{
    ValueTask<IReadOnlyList<OperationStorageCatalogEntry>> ScanAsync(
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(
        OperationStorageCatalogEntry expected,
        CancellationToken cancellationToken);
}

internal interface IBackupRetentionStore
{
    ValueTask<IReadOnlyList<BackupStorageCatalogEntry>> ScanAsync(
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(
        BackupStorageCatalogEntry expected,
        CancellationToken cancellationToken);
}

internal sealed class DurableOperationRetentionStore(
    DurableOperationJournal journal) : IOperationRetentionStore
{
    private readonly DurableOperationJournal _journal =
        journal ?? throw new ArgumentNullException(nameof(journal));

    public ValueTask<IReadOnlyList<OperationStorageCatalogEntry>> ScanAsync(
        CancellationToken cancellationToken) =>
        _journal.ScanStorageCatalogAsync(cancellationToken);

    public ValueTask<bool> DeleteAsync(
        OperationStorageCatalogEntry expected,
        CancellationToken cancellationToken) =>
        _journal.DeleteVerifiedTerminalAsync(expected, cancellationToken);
}

internal sealed class BackupRepositoryRetentionStore(
    BackupRepository repository) : IBackupRetentionStore
{
    private readonly BackupRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));

    public ValueTask<IReadOnlyList<BackupStorageCatalogEntry>> ScanAsync(
        CancellationToken cancellationToken) =>
        _repository.ScanStorageCatalogAsync(cancellationToken);

    public ValueTask<bool> DeleteAsync(
        BackupStorageCatalogEntry expected,
        CancellationToken cancellationToken) =>
        _repository.DeleteVerifiedBackupAsync(
            expected,
            retentionConfirmedUnreferenced: true,
            cancellationToken);
}

internal sealed class WinoraRetentionArtifactStore : IRetentionArtifactStore
{
    private const int MinimumRetainedVerifiedOperationBackups = 50;
    private static readonly TimeSpan MinimumOperationBackupAge = TimeSpan.FromDays(90);

    private readonly IOperationRetentionStore _operations;
    private readonly IBackupRetentionStore _backups;
    private readonly ActionJournal _actionJournal;

    internal WinoraRetentionArtifactStore(
        DurableOperationJournal operations,
        BackupRepository backups,
        ActionJournal actionJournal)
        : this(
            new DurableOperationRetentionStore(operations),
            new BackupRepositoryRetentionStore(backups),
            actionJournal)
    {
    }

    internal WinoraRetentionArtifactStore(
        IOperationRetentionStore operations,
        IBackupRetentionStore backups,
        ActionJournal actionJournal)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _actionJournal = actionJournal ?? throw new ArgumentNullException(nameof(actionJournal));
    }

    public async ValueTask<RetentionArtifactSelection> CaptureAsync(
        ActionJournalRetentionRequest request,
        DateTimeOffset nowUtc,
        int reservedDecisionEventCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ActionJournalSchema.ValidateTimestamp(nowUtc, nameof(nowUtc));
        if (reservedDecisionEventCount < 0 ||
            request.MaximumEventCount < reservedDecisionEventCount)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedDecisionEventCount));
        }

        var operationCatalog = await _operations.ScanAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateOperationCatalog(operationCatalog);
        var linkedState = RetentionLinkedStateSnapshot.Create(
            operationCatalog,
            request.CompletedOperationId);
        if (!linkedState.LinkedOperationIds.ToHashSet()
                .SetEquals(request.LinkedChangeOperationIds))
        {
            throw new InvalidOperationException(
                "The caller linked-operation expectation does not match the verified operation catalog.");
        }

        var (operation, backup) = await CaptureOperationAndBackupAsync(
            request,
            operationCatalog,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        var snapshots = await _actionJournal.ReadVerifiedEventSnapshotsAsync(
            cancellationToken).ConfigureAwait(false);
        var selectedEntries = ActionJournalRetentionPolicy.SelectExpiredEvents(
            snapshots.Select(item => item.Entry).ToArray(),
            linkedState.LinkedOperationIds.ToHashSet(),
            nowUtc,
            request.MaximumAge,
            request.MaximumEventCount - reservedDecisionEventCount);
        var snapshotsById = snapshots.ToDictionary(
            item => item.Entry.EventId,
            StringComparer.Ordinal);
        var actionEvents = selectedEntries.Select(entry =>
        {
            var snapshot = snapshotsById[entry.EventId];
            return new RetentionActionEventIdentity(
                entry.EventId,
                snapshot.PayloadSha256,
                snapshot.Identity.VolumeSerialNumber,
                snapshot.Identity.FileIndex);
        }).ToArray();

        return new RetentionArtifactSelection(
            operation,
            backup,
            Array.AsReadOnly(actionEvents),
            linkedState);
    }

    public async ValueTask VerifyLinkedStateAsync(
        RetentionLinkedStateSnapshot expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        var current = await _operations.ScanAsync(cancellationToken).ConfigureAwait(false);
        ValidateOperationCatalog(current);
        if (!expected.MatchesCatalog(current))
        {
            throw new InvalidDataException(
                "The authoritative linked-operation catalog changed after retention capture.");
        }
    }

    public ValueTask<bool> DeleteOperationAsync(
        RetentionTransactionBoundary boundary,
        CancellationToken cancellationToken)
    {
        ValidateDeletingBoundary(
            boundary,
            RetentionLifecycleState.DeletingOperation);
        var operation = boundary.Intent.Operation ??
            throw new InvalidDataException(
                "The durable operation-deletion state has no exact operation identity.");
        return _operations.DeleteAsync(
            new OperationStorageCatalogEntry(
                operation.OperationId,
                operation.Revision,
                operation.State,
                operation.LastEventHash,
                operation.TerminalOccurredAtUtc,
                operation.PlanDigest,
                operation.BackupId,
                operation.BackupDigest,
                operation.RootVolumeSerialNumber,
                operation.RootFileIndex,
                operation.IsTerminal,
                operation.IsRecoveryProtected),
            cancellationToken);
    }

    public ValueTask<bool> DeleteBackupAsync(
        RetentionTransactionBoundary boundary,
        CancellationToken cancellationToken)
    {
        ValidateDeletingBoundary(
            boundary,
            RetentionLifecycleState.DeletingBackup);
        var backup = boundary.Intent.Backup ??
            throw new InvalidDataException(
                "The durable backup-deletion state has no exact backup identity.");
        return _backups.DeleteAsync(
            new BackupStorageCatalogEntry(
                backup.BackupId,
                backup.Status,
                backup.Kind,
                backup.PlanDigest,
                backup.BackupDigest,
                backup.Protection,
                backup.IsVerified,
                backup.IsRecoveryProtected,
                backup.CommittedUtc),
            cancellationToken);
    }

    public ValueTask<int> DeleteActionEventsAsync(
        RetentionTransactionBoundary boundary,
        IMutationLeaseHandle lease,
        CancellationToken cancellationToken)
    {
        ValidateDeletingBoundary(
            boundary,
            RetentionLifecycleState.DeletingActionEvents);
        return _actionJournal.DeleteFromVerifiedRetentionIntentAsync(
            boundary,
            lease,
            (entry, token) => ValidateActionEventLinkedStateAsync(
                boundary.Intent.RehydrateLinkedState(),
                entry,
                token),
            cancellationToken);
    }

    public async ValueTask RebuildActionIndexAsync(CancellationToken cancellationToken) =>
        _ = await _actionJournal.RebuildIndexAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask ValidateActionEventLinkedStateAsync(
        RetentionLinkedStateSnapshot expected,
        ActionJournalEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Status is not (
                ActionJournalStatus.Failed or
                ActionJournalStatus.RecoveryRequired or
                ActionJournalStatus.RollbackFailed))
        {
            return;
        }

        await VerifyLinkedStateAsync(expected, cancellationToken).ConfigureAwait(false);
        if (expected.LinkedOperationIds.Contains(entry.OperationId))
        {
            throw new InvalidDataException(
                "A retained failure event is linked by the authoritative operation catalog.");
        }
    }

    private async ValueTask<(
        RetentionOperationIdentity? Operation,
        RetentionBackupIdentity? Backup)> CaptureOperationAndBackupAsync(
        ActionJournalRetentionRequest request,
        IReadOnlyList<OperationStorageCatalogEntry> operationCatalog,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completedOperationId = request.CompletedOperationId;
        if (completedOperationId is null)
        {
            return (null, null);
        }

        if (request.LinkedChangeOperationIds.Contains(completedOperationId.Value))
        {
            throw new InvalidOperationException(
                "A completed operation linked to an existing change cannot be selected for retention deletion.");
        }

        var operation = operationCatalog.SingleOrDefault(
            item => item.OperationId == completedOperationId.Value) ??
            throw new InvalidOperationException(
                "The requested operation has no verified storage-catalog identity.");
        ValidateAuthoritativeUtc(
            operation.TerminalOccurredAtUtc,
            "The terminal operation timestamp is not authoritative UTC.");
        if (!operation.IsTerminal ||
            !OperationStatePolicy.IsTerminal(operation.State) ||
            operation.IsRecoveryProtected)
        {
            throw new InvalidOperationException(
                "Incomplete or recovery-protected operation evidence cannot be retained for deletion.");
        }

        var operationIdentity = new RetentionOperationIdentity(
            operation.OperationId,
            operation.Revision,
            operation.State,
            operation.LastEventHash,
            operation.TerminalOccurredAtUtc,
            operation.PlanDigest,
            operation.BackupId,
            operation.BackupDigest,
            operation.RootVolumeSerialNumber,
            operation.RootFileIndex,
            operation.IsTerminal,
            operation.IsRecoveryProtected);
        if (operation.BackupId is null && operation.BackupDigest is null)
        {
            return (operationIdentity, null);
        }

        if (operation.BackupId is null || operation.BackupDigest is null)
        {
            throw new InvalidDataException(
                "The terminal operation has an incomplete durable backup binding.");
        }

        if (operationCatalog.Any(item =>
                item.OperationId != operation.OperationId &&
                StringComparer.Ordinal.Equals(item.BackupId, operation.BackupId)))
        {
            throw new InvalidOperationException(
                "A backup referenced by another active, recovery, or linked operation cannot be selected.");
        }

        var backupCatalog = await _backups.ScanAsync(cancellationToken).ConfigureAwait(false);
        if (backupCatalog
            .GroupBy(item => item.BackupId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "The verified backup storage catalog contains duplicate identities.");
        }

        var verifiedOperationBackups = backupCatalog
            .Where(IsVerifiedOperationBackup)
            .ToArray();
        foreach (var candidate in verifiedOperationBackups)
        {
            if (candidate.CommittedUtc is not { } candidateCommittedUtc)
            {
                throw new InvalidDataException(
                    "A verified operation backup is missing its authoritative committed timestamp.");
            }

            ValidateAuthoritativeUtc(
                candidateCommittedUtc,
                "A verified operation backup timestamp is not authoritative UTC.");
        }

        var backup = backupCatalog.SingleOrDefault(
            item => StringComparer.Ordinal.Equals(item.BackupId, operation.BackupId)) ??
            throw new InvalidOperationException(
                "The operation's linked backup has no verified storage-catalog identity.");
        if (backup.Status != BackupStorageStatus.VerifiedCommitted ||
            backup.Kind != BackupCaptureKind.Operation ||
            !backup.IsVerified ||
            backup.CommittedUtc is not { } committedUtc ||
            committedUtc == default ||
            committedUtc.Offset != TimeSpan.Zero ||
            backup.Protection != BackupProtectionClass.OperationRollbackSource ||
            !StringComparer.Ordinal.Equals(backup.PlanDigest, operation.PlanDigest) ||
            !StringComparer.Ordinal.Equals(backup.BackupDigest, operation.BackupDigest))
        {
            throw new InvalidDataException(
                "The linked backup does not exactly match the terminal operation binding.");
        }

        var cutoffUtc = nowUtc - MinimumOperationBackupAge;
        if (operation.TerminalOccurredAtUtc > cutoffUtc || committedUtc > cutoffUtc)
        {
            throw new InvalidOperationException(
                "The linked operation and backup have not both reached the minimum retention age.");
        }

        var eligibleByRank = verifiedOperationBackups
            .OrderByDescending(item => item.CommittedUtc!.Value)
            .ThenBy(item => item.BackupId, StringComparer.Ordinal)
            .Skip(MinimumRetainedVerifiedOperationBackups)
            .Any(item => StringComparer.Ordinal.Equals(item.BackupId, backup.BackupId));
        if (!eligibleByRank)
        {
            throw new InvalidOperationException(
                "The requested backup is among the newest fifty verified operation backups.");
        }

        return (
            operationIdentity,
            new RetentionBackupIdentity(
                backup.BackupId,
                backup.Status,
                backup.Kind.Value,
                backup.PlanDigest!,
                backup.BackupDigest!,
                backup.Protection,
                backup.IsVerified,
                backup.IsRecoveryProtected,
                committedUtc));
    }

    private static bool IsVerifiedOperationBackup(BackupStorageCatalogEntry entry) =>
        entry.Status == BackupStorageStatus.VerifiedCommitted &&
        entry.Kind == BackupCaptureKind.Operation &&
        entry.IsVerified &&
        entry.Protection == BackupProtectionClass.OperationRollbackSource;

    private static void ValidateOperationCatalog(
        IReadOnlyList<OperationStorageCatalogEntry> operationCatalog)
    {
        ArgumentNullException.ThrowIfNull(operationCatalog);
        if (operationCatalog.Any(item => item is null) ||
            operationCatalog.GroupBy(item => item.OperationId)
                .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "The verified operation storage catalog contains duplicate or null identities.");
        }
    }

    private static void ValidateAuthoritativeUtc(
        DateTimeOffset value,
        string message)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(message);
        }
    }

    private static void ValidateDeletingBoundary(
        RetentionTransactionBoundary boundary,
        RetentionLifecycleState requiredState)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        RetentionMaintenanceSchema.Validate(boundary.Intent);
        if (boundary.State != requiredState ||
            boundary.Revision <= 0 ||
            boundary.LeaseId == Guid.Empty ||
            boundary.LeaseEpoch <= 0)
        {
            throw new InvalidOperationException(
                "Artifact deletion requires the exact verified durable deleting state.");
        }
    }
}
