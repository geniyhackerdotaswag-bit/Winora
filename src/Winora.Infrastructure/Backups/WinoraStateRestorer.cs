using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Winora.Core.Contracts;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Backups;

internal sealed record WinoraStateRestorePublicationContext(
    string TargetPath,
    string TemporaryPath,
    string LastKnownGoodPath);

internal interface IWinoraStateRestoreRaceHook
{
    void AfterInitialTargetValidation(WinoraStateRestorePublicationContext context);

    void AfterPublicationBeforeJournal(WinoraStateRestorePublicationContext context)
    {
    }
}

internal sealed class WinoraStateRestorer
{
    private readonly WinoraDataPaths _paths;
    private readonly IAtomicFileOperations _fileOperations;
    private readonly IWriteThroughPublisher _publisher;
    private readonly IHandleDurability _handleDurability;
    private readonly IFileDurability _durability;
    private readonly IValidatedFileAccess _validatedFileAccess;
    private readonly IWinoraStateRestoreRaceHook? _publicationRaceHook;
    private readonly WinoraStateRestoreRecoveryStore _recoveryStore;
    private readonly TimeProvider _timeProvider;

    internal WinoraStateRestorer(
        WinoraDataPaths paths,
        IAtomicFileOperations? fileOperations = null,
        IFileDurability? durability = null,
        IValidatedFileAccess? validatedFileAccess = null,
        IWinoraStateRestoreRaceHook? publicationRaceHook = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileOperations = fileOperations ?? new WindowsAtomicFileOperations();
        _publisher = new WriteThroughPublisher(_fileOperations);
        _handleDurability = new WindowsHandleDurability();
        _durability = durability ?? new WindowsFileDurability();
        _validatedFileAccess = validatedFileAccess ?? new WindowsValidatedFileAccess();
        _publicationRaceHook = publicationRaceHook;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _recoveryStore = new WinoraStateRestoreRecoveryStore(paths, _timeProvider);
    }

    internal void Restore(
        IReadOnlyList<BackupArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var prepared = new List<PreparedRestore>(artifacts.Count);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safeToCleanRecoveryFiles = false;
        var applyCleanupBoundaryPersisted = false;
        WinoraStateRestoreRecoveryDocument? recovery = null;
        Exception? operationFailure = null;
        try
        {
            var existing = _recoveryStore.ReadAsync(cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (existing is { IsTerminal: false })
            {
                throw new InvalidOperationException(
                    "An incomplete Winora-state restore must be recovered before another restore starts.");
            }

            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = GetAllowedTargetPath(artifact.Key);
                if (!targets.Add(targetPath))
                {
                    throw new InvalidDataException(
                        "A Winora-state restore contains a duplicate target.");
                }

                prepared.Add(Prepare(
                    artifact.Key,
                    targetPath,
                    artifact.Content,
                    cancellationToken));
            }

            if (prepared.Count == 0)
            {
                safeToCleanRecoveryFiles = true;
                return;
            }

            recovery = CreateRecoveryDocument(prepared);
            PersistRecovery(recovery, cancellationToken);
            for (var index = 0; index < prepared.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                recovery = recovery.WithEntryStatus(
                    index,
                    WinoraStateRestoreEntryStatus.Applying,
                    WinoraStateRestoreRecoveryStatus.Applying);
                PersistRecovery(recovery, cancellationToken);
                Publish(prepared[index], cancellationToken);
                recovery = recovery.WithEntryStatus(
                    index,
                    WinoraStateRestoreEntryStatus.Applied,
                    WinoraStateRestoreRecoveryStatus.Applying);
                PersistRecovery(recovery, CancellationToken.None);
            }

            var applyCleanup = recovery with
            {
                Status = WinoraStateRestoreRecoveryStatus.CleanupAfterApply,
            };
            PersistRecovery(applyCleanup, CancellationToken.None);
            recovery = applyCleanup;
            applyCleanupBoundaryPersisted = true;
            CleanupAfterApply(prepared);
            var completed = recovery with
            {
                Status = WinoraStateRestoreRecoveryStatus.Completed,
            };
            PersistRecovery(completed, CancellationToken.None);
            recovery = completed;
            safeToCleanRecoveryFiles = true;
        }
        catch (Exception primaryFailure)
        {
            if (recovery is not null && !applyCleanupBoundaryPersisted)
            {
                var rollback = RestorePreviousFiles(prepared, recovery);
                recovery = rollback.Recovery;
                var rollbackFailures = rollback.Failures;
                safeToCleanRecoveryFiles = rollbackFailures.Count == 0;
                if (rollbackFailures.Count > 0)
                {
                    operationFailure = new AggregateException(
                        "Winora-state restore failed and compensating rollback requires recovery.",
                        new[] { primaryFailure }.Concat(rollbackFailures));
                }
                else
                {
                    operationFailure = primaryFailure;
                }
            }
            else
            {
                operationFailure = primaryFailure;
            }
        }

        var cleanupFailures = new List<Exception>();
        foreach (var item in prepared)
        {
            try
            {
                item.Dispose(safeToCleanRecoveryFiles);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (operationFailure is not null && cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                "Winora-state restore and safe cleanup both failed.",
                new[] { operationFailure }.Concat(cleanupFailures));
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                "Winora-state restore completed but safe temporary-file cleanup failed.",
                cleanupFailures);
        }
    }

    private PreparedRestore Prepare(
        string logicalKey,
        string targetPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var parentLease = SecureOwnedPathLease.Acquire(_paths, targetPath);
        string? temporaryPath = null;
        ValidatedFileHandle? stagingHandle = null;
        try
        {
            var target = ReadSnapshot(
                targetPath,
                ValidatedFileUse.ProjectionProbe,
                flushToDisk: false);
            var parent = Path.GetDirectoryName(targetPath) ??
                throw new InvalidDataException("A Winora-state target has no parent directory.");
            temporaryPath = Path.Combine(
                parent,
                $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.restore.tmp");
            var lastKnownGoodPath =
                $"{targetPath}.{Guid.NewGuid():N}.restore.lkg";
            WriteTemporary(temporaryPath, content, cancellationToken);
            stagingHandle = _validatedFileAccess.OpenForMutation(
                temporaryPath,
                ValidatedFileUse.StagingReadback);
            var readback = stagingHandle.ReadAllBytes(flushToDisk: true);
            var expectedHash = SHA256.HashData(content.Span);
            if (readback.Length != content.Length ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(readback),
                    expectedHash))
            {
                throw new InvalidDataException(
                    "A restore staging file failed durable readback verification.");
            }

            return new PreparedRestore(
                logicalKey,
                targetPath,
                temporaryPath,
                lastKnownGoodPath,
                target,
                stagingHandle,
                new FileSnapshot(
                    stagingHandle.Identity,
                    readback.LongLength,
                    Convert.ToHexString(SHA256.HashData(readback))),
                parentLease,
                _fileOperations);
        }
        catch
        {
            var stagingIdentity = stagingHandle?.Identity;
            stagingHandle?.Dispose();
            if (temporaryPath is not null && stagingIdentity is { } identity)
            {
                SecureBackupDirectoryLayout.DeleteSingleFileWithoutFollowingReparsePoints(
                    temporaryPath,
                    identity);
            }

            parentLease.Dispose();
            throw;
        }
    }

    private void Publish(
        PreparedRestore item,
        CancellationToken cancellationToken)
    {
        var publicationContext = new WinoraStateRestorePublicationContext(
            item.TargetPath,
            item.TemporaryPath,
            item.LastKnownGoodPath);
        EnsureSnapshotUnchanged(item.TargetPath, item.OriginalTarget);
        _publicationRaceHook?.AfterInitialTargetValidation(
            publicationContext);
        PinAndValidateCurrentTarget(item);
        EnsurePinnedSnapshotUnchanged(
            item.StagingHandle,
            item.Staging,
            "The restore staging file changed before publication.");
        EnsureMissing(item.LastKnownGoodPath);
        cancellationToken.ThrowIfCancellationRequested();

        item.PublicationAttempted = true;
        try
        {
            if (item.OriginalTarget is not null)
            {
                _publisher.ReplaceProjectionAsync(
                    item.StagingHandle,
                    item.OriginalTargetHandle!,
                    item.TargetPath,
                    existingLastKnownGoodFile: null,
                    item.LastKnownGoodPath,
                    Convert.FromHexString(item.Staging.Sha256),
                    cancellationToken).GetAwaiter().GetResult();
            }
            else
            {
                _publisher.PublishNewAsync(
                    item.StagingHandle,
                    item.TargetPath,
                    Convert.FromHexString(item.Staging.Sha256),
                    cancellationToken).GetAwaiter().GetResult();
            }

            item.Published = true;
        }
        catch
        {
            item.Published = item.HasSystemMutation;
            throw;
        }

        _publicationRaceHook?.AfterPublicationBeforeJournal(publicationContext);
        VerifyPublication(item);
    }

    private void VerifyPublication(PreparedRestore item)
    {
        EnsurePinnedSnapshotUnchanged(
            item.StagingHandle,
            item.Staging,
            "The restored target changed during publication verification.",
            flushToDisk: true,
            ValidatedFileUse.PostPublication);
        if (item.OriginalTarget is not null)
        {
            EnsurePinnedSnapshotUnchanged(
                item.OriginalTargetHandle!,
                item.OriginalTarget,
                "The retained original target changed during publication verification.",
                observedUse: ValidatedFileUse.PostPublication);
        }
    }

    private void PinAndValidateCurrentTarget(PreparedRestore item)
    {
        var target = _validatedFileAccess.TryOpenForMutation(
            item.TargetPath,
            ValidatedFileUse.PrePublication);
        if (item.OriginalTarget is null)
        {
            if (target is null)
            {
                return;
            }

            target.Dispose();
            throw new InvalidDataException(
                "A Winora-state target appeared after restore preflight.");
        }

        if (target is null)
        {
            throw new InvalidDataException(
                "A Winora-state target disappeared after restore preflight.");
        }

        try
        {
            EnsurePinnedSnapshotUnchanged(
                target,
                item.OriginalTarget,
                "A Winora-state target changed after restore preflight.");
            item.SetOriginalTargetHandle(target);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static void CleanupAfterApply(IReadOnlyList<PreparedRestore> prepared)
    {
        var failures = new List<Exception>();
        foreach (var item in prepared)
        {
            try
            {
                item.DeleteRetainedOriginal();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more applied Winora-state restore sidecars could not be cleaned safely.",
                failures);
        }
    }

    private RollbackOutcome RestorePreviousFiles(
        IReadOnlyList<PreparedRestore> prepared,
        WinoraStateRestoreRecoveryDocument recovery)
    {
        var failures = new List<Exception>();
        for (var index = prepared.Count - 1; index >= 0; index--)
        {
            var item = prepared[index];
            try
            {
                if (item.Published)
                {
                    recovery = recovery.WithEntryStatus(
                        index,
                        WinoraStateRestoreEntryStatus.RollingBack,
                        WinoraStateRestoreRecoveryStatus.RollingBack);
                    PersistRecovery(recovery, CancellationToken.None);
                    RestorePreviousFile(item);
                }

                recovery = recovery.WithEntryStatus(
                    index,
                    WinoraStateRestoreEntryStatus.RolledBack,
                    WinoraStateRestoreRecoveryStatus.RollingBack);
                PersistRecovery(recovery, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            var recoveryRequired = recovery with
            {
                Status = WinoraStateRestoreRecoveryStatus.RecoveryRequired,
            };
            try
            {
                PersistRecovery(recoveryRequired, CancellationToken.None);
                recovery = recoveryRequired;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            return new RollbackOutcome(
                recovery,
                Array.AsReadOnly(failures.ToArray()));
        }

        var cleanup = recovery with
        {
            Status = WinoraStateRestoreRecoveryStatus.CleanupAfterRollback,
        };
        try
        {
            PersistRecovery(cleanup, CancellationToken.None);
            recovery = cleanup;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            return new RollbackOutcome(
                recovery,
                Array.AsReadOnly(failures.ToArray()));
        }

        foreach (var item in prepared)
        {
            try
            {
                item.DeleteRolledBackStaging();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count == 0)
        {
            var rolledBack = recovery with
            {
                Status = WinoraStateRestoreRecoveryStatus.RolledBack,
            };
            try
            {
                PersistRecovery(rolledBack, CancellationToken.None);
                recovery = rolledBack;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return new RollbackOutcome(
            recovery,
            Array.AsReadOnly(failures.ToArray()));
    }

    private void RestorePreviousFile(PreparedRestore item)
    {
        if (item.OriginalTarget is null)
        {
            if (PathsEqual(item.StagingHandle.CurrentPath, item.TargetPath))
            {
                EnsurePinnedSnapshotUnchanged(
                    item.StagingHandle,
                    item.Staging,
                    "The newly restored target changed before rollback.");
                _publisher.PublishNewAsync(
                    item.StagingHandle,
                    item.TemporaryPath,
                    Convert.FromHexString(item.Staging.Sha256),
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            if (!PathsEqual(item.StagingHandle.CurrentPath, item.TemporaryPath))
            {
                throw new InvalidDataException(
                    "The newly restored target moved to an unknown path before rollback.");
            }

            EnsureSnapshotUnchanged(item.TargetPath, expected: null);
            item.RolledBack = true;
            return;
        }

        var originalHandle = item.OriginalTargetHandle ??
            throw new InvalidOperationException(
                "The pinned original target was released before rollback.");
        if (PathsEqual(originalHandle.CurrentPath, item.TargetPath))
        {
            item.RolledBack = true;
            return;
        }

        if (!PathsEqual(item.StagingHandle.CurrentPath, item.TargetPath) ||
            !PathsEqual(originalHandle.CurrentPath, item.LastKnownGoodPath))
        {
            var current = ReadSnapshot(
                item.TargetPath,
                ValidatedFileUse.PrePublication,
                flushToDisk: false);
            throw new InvalidDataException(
                current == item.Staging
                    ? "The pinned restore handles no longer match their published paths."
                    : "The restored target changed before rollback.");
        }

        EnsurePinnedSnapshotUnchanged(
            item.StagingHandle,
            item.Staging,
            "The restored target changed before rollback.");
        EnsurePinnedSnapshotUnchanged(
            originalHandle,
            item.OriginalTarget,
            "The retained original target changed before rollback.");
        _publisher.ReplaceProjectionAsync(
            originalHandle,
            item.StagingHandle,
            item.TargetPath,
            existingLastKnownGoodFile: null,
            item.TemporaryPath,
            Convert.FromHexString(item.OriginalTarget.Sha256),
            CancellationToken.None).GetAwaiter().GetResult();
        EnsurePinnedSnapshotUnchanged(
            originalHandle,
            item.OriginalTarget,
            "The original target failed rollback verification.",
            flushToDisk: true,
            ValidatedFileUse.PostPublication);
        EnsurePinnedSnapshotUnchanged(
            item.StagingHandle,
            item.Staging,
            "The displaced restored target failed rollback verification.",
            observedUse: ValidatedFileUse.PostPublication);
        item.RolledBack = true;
    }

    internal async ValueTask<WinoraStateRestoreRecoveryInfo?> InspectPendingRecoveryAsync(
        CancellationToken cancellationToken)
    {
        var recovery = await _recoveryStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        return recovery is null || recovery.IsTerminal
            ? null
            : recovery.ToInfo();
    }

    internal async ValueTask RecoverPendingAsync(CancellationToken cancellationToken)
    {
        var recovery = await _recoveryStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (recovery is null || recovery.IsTerminal)
        {
            return;
        }

        try
        {
            if (recovery.Status == WinoraStateRestoreRecoveryStatus.CleanupAfterApply)
            {
                await CompletePendingApplyCleanupAsync(
                    recovery,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (recovery.Status == WinoraStateRestoreRecoveryStatus.CleanupAfterRollback)
            {
                await CompletePendingRollbackCleanupAsync(
                    recovery,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            for (var index = recovery.Entries.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = recovery.Entries[index];
                if (entry.Status == WinoraStateRestoreEntryStatus.RolledBack)
                {
                    continue;
                }

                var targetPath = GetAllowedTargetPath(entry.LogicalKey);
                using var parentLease = SecureOwnedPathLease.Acquire(_paths, targetPath);
                var temporaryPath = ResolveRecoveryLeaf(targetPath, entry.TemporaryFileName);
                var lastKnownGoodPath = ResolveRecoveryLeaf(
                    targetPath,
                    entry.LastKnownGoodFileName);
                var original = entry.OriginalTarget is null
                    ? null
                    : FileSnapshot.FromDocument(entry.OriginalTarget);
                var staging = FileSnapshot.FromDocument(entry.Staging);
                var current = ReadSnapshot(
                    targetPath,
                    ValidatedFileUse.PrePublication,
                    flushToDisk: false);

                if (current == staging || current == original || current is null)
                {
                    recovery = recovery.WithEntryStatus(
                        index,
                        WinoraStateRestoreEntryStatus.RollingBack,
                        WinoraStateRestoreRecoveryStatus.RollingBack);
                    await _recoveryStore.WriteAsync(
                        recovery,
                        CancellationToken.None).ConfigureAwait(false);
                    RestorePersistedFile(
                        targetPath,
                        temporaryPath,
                        lastKnownGoodPath,
                        original,
                        staging);
                }
                else
                {
                    throw new InvalidDataException(
                        "Pending Winora-state restore recovery found external target drift.");
                }

                recovery = recovery.WithEntryStatus(
                    index,
                    WinoraStateRestoreEntryStatus.RolledBack,
                    WinoraStateRestoreRecoveryStatus.RollingBack);
                await _recoveryStore.WriteAsync(
                    recovery,
                    CancellationToken.None).ConfigureAwait(false);
            }

            var cleanup = recovery with
            {
                Status = WinoraStateRestoreRecoveryStatus.CleanupAfterRollback,
            };
            await _recoveryStore.WriteAsync(
                cleanup,
                CancellationToken.None).ConfigureAwait(false);
            recovery = cleanup;
            await CompletePendingRollbackCleanupAsync(
                recovery,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryFailure)
        {
            Exception? journalFailure = null;
            try
            {
                var failureStatus = recovery.Status is
                    WinoraStateRestoreRecoveryStatus.CleanupAfterApply or
                    WinoraStateRestoreRecoveryStatus.CleanupAfterRollback
                        ? recovery.Status
                        : WinoraStateRestoreRecoveryStatus.RecoveryRequired;
                recovery = recovery with
                {
                    Status = failureStatus,
                };
                await _recoveryStore.WriteAsync(
                    recovery,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                journalFailure = exception;
            }

            if (journalFailure is not null)
            {
                throw new AggregateException(
                    "Winora-state recovery and its durable status update both failed.",
                    primaryFailure,
                    journalFailure);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private async ValueTask CompletePendingApplyCleanupAsync(
        WinoraStateRestoreRecoveryDocument recovery,
        CancellationToken cancellationToken)
    {
        foreach (var entry in recovery.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanupPersistedAppliedFile(entry);
        }

        var completed = recovery with
        {
            Status = WinoraStateRestoreRecoveryStatus.Completed,
        };
        await _recoveryStore.WriteAsync(
            completed,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask CompletePendingRollbackCleanupAsync(
        WinoraStateRestoreRecoveryDocument recovery,
        CancellationToken cancellationToken)
    {
        foreach (var entry in recovery.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanupPersistedRolledBackFile(entry);
        }

        var rolledBack = recovery with
        {
            Status = WinoraStateRestoreRecoveryStatus.RolledBack,
        };
        await _recoveryStore.WriteAsync(
            rolledBack,
            CancellationToken.None).ConfigureAwait(false);
    }

    private void CleanupPersistedAppliedFile(WinoraStateRestoreEntryDocument entry)
    {
        if (entry.Status != WinoraStateRestoreEntryStatus.Applied)
        {
            throw new InvalidDataException(
                "Apply cleanup was requested before every restore entry was durably applied.");
        }

        var targetPath = GetAllowedTargetPath(entry.LogicalKey);
        using var parentLease = SecureOwnedPathLease.Acquire(_paths, targetPath);
        var temporaryPath = ResolveRecoveryLeaf(targetPath, entry.TemporaryFileName);
        var lastKnownGoodPath = ResolveRecoveryLeaf(
            targetPath,
            entry.LastKnownGoodFileName);
        var staging = FileSnapshot.FromDocument(entry.Staging);
        using var target = _validatedFileAccess.OpenForMutation(
            targetPath,
            ValidatedFileUse.PrePublication);
        EnsurePinnedSnapshotUnchanged(
            target,
            staging,
            "The applied Winora-state target changed before cleanup.");
        EnsureSnapshotUnchanged(temporaryPath, expected: null);

        using var retainedOriginal = _validatedFileAccess.TryOpenForMutation(
            lastKnownGoodPath,
            ValidatedFileUse.PrePublication);
        if (entry.OriginalTarget is null)
        {
            if (retainedOriginal is not null)
            {
                throw new InvalidDataException(
                    "Apply cleanup found an unexpected original-target sidecar.");
            }
        }
        else if (retainedOriginal is not null)
        {
            var original = FileSnapshot.FromDocument(entry.OriginalTarget);
            EnsurePinnedSnapshotUnchanged(
                retainedOriginal,
                original,
                "The retained original target changed before apply cleanup.");
            _fileOperations.Delete(retainedOriginal);
        }

        EnsurePinnedSnapshotUnchanged(
            target,
            staging,
            "The applied Winora-state target changed during cleanup.");
    }

    private void CleanupPersistedRolledBackFile(WinoraStateRestoreEntryDocument entry)
    {
        if (entry.Status != WinoraStateRestoreEntryStatus.RolledBack)
        {
            throw new InvalidDataException(
                "Rollback cleanup was requested before every restore entry was durably rolled back.");
        }

        var targetPath = GetAllowedTargetPath(entry.LogicalKey);
        using var parentLease = SecureOwnedPathLease.Acquire(_paths, targetPath);
        var temporaryPath = ResolveRecoveryLeaf(targetPath, entry.TemporaryFileName);
        var lastKnownGoodPath = ResolveRecoveryLeaf(
            targetPath,
            entry.LastKnownGoodFileName);
        var original = entry.OriginalTarget is null
            ? null
            : FileSnapshot.FromDocument(entry.OriginalTarget);
        ValidatedFileHandle? target = null;
        try
        {
            if (original is null)
            {
                EnsureSnapshotUnchanged(targetPath, expected: null);
            }
            else
            {
                target = _validatedFileAccess.OpenForMutation(
                    targetPath,
                    ValidatedFileUse.PrePublication);
                EnsurePinnedSnapshotUnchanged(
                    target,
                    original,
                    "The rolled-back Winora-state target changed before cleanup.");
            }

            EnsureSnapshotUnchanged(lastKnownGoodPath, expected: null);
            var staging = FileSnapshot.FromDocument(entry.Staging);
            using var displacedStaging = _validatedFileAccess.TryOpenForMutation(
                temporaryPath,
                ValidatedFileUse.PrePublication);
            if (displacedStaging is not null)
            {
                EnsurePinnedSnapshotUnchanged(
                    displacedStaging,
                    staging,
                    "The displaced restore staging file changed before rollback cleanup.");
                _fileOperations.Delete(displacedStaging);
            }

            if (target is null)
            {
                EnsureSnapshotUnchanged(targetPath, expected: null);
            }
            else
            {
                EnsurePinnedSnapshotUnchanged(
                    target,
                    original!,
                    "The rolled-back Winora-state target changed during cleanup.");
            }
        }
        finally
        {
            target?.Dispose();
        }
    }

    private WinoraStateRestoreRecoveryDocument CreateRecoveryDocument(
        IReadOnlyList<PreparedRestore> prepared)
    {
        var entries = prepared.Select(item => new WinoraStateRestoreEntryDocument(
            item.LogicalKey,
            Path.GetFileName(item.TemporaryPath),
            Path.GetFileName(item.LastKnownGoodPath),
            item.OriginalTarget?.ToDocument(),
            item.Staging.ToDocument(),
            WinoraStateRestoreEntryStatus.Prepared)).ToArray();
        return new WinoraStateRestoreRecoveryDocument(
            Guid.NewGuid(),
            _timeProvider.GetUtcNow().ToUniversalTime(),
            WinoraStateRestoreRecoveryStatus.Prepared,
            Array.AsReadOnly(entries));
    }

    private void PersistRecovery(
        WinoraStateRestoreRecoveryDocument recovery,
        CancellationToken cancellationToken) =>
        _recoveryStore.WriteAsync(recovery, cancellationToken)
            .AsTask().GetAwaiter().GetResult();

    private void RestorePersistedFile(
        string targetPath,
        string temporaryPath,
        string lastKnownGoodPath,
        FileSnapshot? original,
        FileSnapshot staging)
    {
        var current = ReadSnapshot(
            targetPath,
            ValidatedFileUse.PrePublication,
            flushToDisk: false);
        var temporary = ReadSnapshot(
            temporaryPath,
            ValidatedFileUse.PrePublication,
            flushToDisk: false);
        var lastKnownGood = ReadSnapshot(
            lastKnownGoodPath,
            ValidatedFileUse.PrePublication,
            flushToDisk: false);
        if (original is null)
        {
            if (lastKnownGood is not null)
            {
                throw new InvalidDataException(
                    "Recovery found an unexpected original-target sidecar for a previously missing file.");
            }

            if (current == staging)
            {
                if (temporary is not null)
                {
                    throw new InvalidDataException(
                        "Recovery found duplicate restore staging identities.");
                }

                using var restoredHandle = _validatedFileAccess.OpenForMutation(
                    targetPath,
                    ValidatedFileUse.PrePublication);
                EnsurePinnedSnapshotUnchanged(
                    restoredHandle,
                    staging,
                    "The restored target changed before recovery rollback.");
                _publisher.PublishNewAsync(
                    restoredHandle,
                    temporaryPath,
                    Convert.FromHexString(staging.Sha256),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            else if (current is not null)
            {
                throw new InvalidDataException(
                    "Pending recovery found external drift at a previously missing target.");
            }
            else if (temporary is not null)
            {
                EnsureEqual(
                    staging,
                    temporary,
                    "The retained restore staging file changed before recovery.");
            }

            EnsureSnapshotUnchanged(targetPath, expected: null);
            return;
        }

        if (current == original)
        {
            if (lastKnownGood is not null)
            {
                throw new InvalidDataException(
                    "Recovery found an unexpected duplicate original-target sidecar.");
            }

            if (temporary is not null)
            {
                EnsureEqual(
                    staging,
                    temporary,
                    "The displaced restore staging file changed before cleanup.");
            }

            return;
        }

        EnsureEqual(
            original,
            lastKnownGood,
            "The retained original target changed before recovery rollback.");
        if (current == staging)
        {
            if (temporary is not null)
            {
                throw new InvalidDataException(
                    "Recovery found duplicate restore staging identities.");
            }

            using var restoredHandle = _validatedFileAccess.OpenForMutation(
                targetPath,
                ValidatedFileUse.PrePublication);
            using var originalHandle = _validatedFileAccess.OpenForMutation(
                lastKnownGoodPath,
                ValidatedFileUse.PrePublication);
            EnsurePinnedSnapshotUnchanged(
                restoredHandle,
                staging,
                "The restored target changed before recovery rollback.");
            EnsurePinnedSnapshotUnchanged(
                originalHandle,
                original,
                "The retained original target changed before recovery rollback.");
            _publisher.ReplaceProjectionAsync(
                originalHandle,
                restoredHandle,
                targetPath,
                existingLastKnownGoodFile: null,
                temporaryPath,
                Convert.FromHexString(original.Sha256),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        else if (current is null)
        {
            if (temporary is not null)
            {
                EnsureEqual(
                    staging,
                    temporary,
                    "The displaced restore staging file changed before recovery rollback.");
            }

            using var originalHandle = _validatedFileAccess.OpenForMutation(
                lastKnownGoodPath,
                ValidatedFileUse.PrePublication);
            EnsurePinnedSnapshotUnchanged(
                originalHandle,
                original,
                "The retained original target changed before recovery rollback.");
            _publisher.PublishNewAsync(
                originalHandle,
                targetPath,
                Convert.FromHexString(original.Sha256),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        else
        {
            throw new InvalidDataException(
                "Pending recovery found external drift at the restored target.");
        }

        EnsureSnapshotUnchanged(
            targetPath,
            original,
            ValidatedFileUse.PostPublication,
            flushToDisk: true);
        EnsureSnapshotUnchanged(lastKnownGoodPath, expected: null);
    }

    private void DeleteStagingIfPresent(string path, FileSnapshot staging)
    {
        var current = ReadSnapshot(
            path,
            ValidatedFileUse.PrePublication,
            flushToDisk: false);
        if (current is null)
        {
            return;
        }

        EnsureEqual(staging, current, "A pending restore staging file changed before cleanup.");
        DeleteExactFile(path, staging);
    }

    private void DeleteExactFile(string path, FileSnapshot expected)
    {
        using var handle = _validatedFileAccess.OpenForMutation(
            path,
            ValidatedFileUse.PrePublication);
        EnsurePinnedSnapshotUnchanged(
            handle,
            expected,
            "A Winora-state recovery file changed before exact deletion.");
        _fileOperations.Delete(handle);
    }

    private string ResolveRecoveryLeaf(string targetPath, string leafName)
    {
        if (!StringComparer.Ordinal.Equals(Path.GetFileName(leafName), leafName))
        {
            throw new InvalidDataException(
                "A pending restore recovery filename escaped its target parent.");
        }

        var parent = Path.GetDirectoryName(targetPath) ??
            throw new InvalidDataException("A pending restore target has no parent directory.");
        return _paths.EnsureOwnedFilePath(Path.Combine(parent, leafName));
    }

    private string GetAllowedTargetPath(string relativePath)
    {
        var normalized = BackupArtifactPath.Normalize(relativePath);
        if (!normalized.StartsWith("data/", StringComparison.Ordinal) &&
            !normalized.StartsWith("assets/", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A Winora-state restore may write only the Data and Assets directories.");
        }

        var relative = normalized.StartsWith("data/", StringComparison.Ordinal)
            ? Path.Combine("Data", normalized["data/".Length..])
            : Path.Combine("Assets", normalized["assets/".Length..]);
        return BackupArtifactPath.CombineUnder(_paths.RootDirectory, relative);
    }

    private void WriteTemporary(
        string temporaryPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        stream.Write(content.Span);
        stream.Flush();
        _durability.FlushToDisk(stream);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private FileSnapshot? ReadSnapshot(
        string path,
        ValidatedFileUse use,
        bool flushToDisk)
    {
        using var file = _validatedFileAccess.TryOpen(
            path,
            flushToDisk ? FileAccess.ReadWrite : FileAccess.Read,
            use);
        if (file is null)
        {
            return null;
        }

        var bytes = file.ReadAllBytes(flushToDisk);
        return new FileSnapshot(
            file.Identity,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static void EnsurePinnedSnapshotUnchanged(
        ValidatedFileHandle handle,
        FileSnapshot expected,
        string message,
        bool flushToDisk = false,
        ValidatedFileUse? observedUse = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var bytes = handle.ReadAllBytes(flushToDisk, observedUse);
        var actual = new FileSnapshot(
            handle.Identity,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)));
        if (actual != expected)
        {
            throw new InvalidDataException(message);
        }
    }

    private void EnsureSnapshotUnchanged(
        string path,
        FileSnapshot? expected,
        ValidatedFileUse use = ValidatedFileUse.PrePublication,
        bool flushToDisk = false)
    {
        var actual = ReadSnapshot(path, use, flushToDisk);
        if (actual != expected)
        {
            throw new InvalidDataException(
                "A Winora-state file identity or content changed during restore preflight.");
        }
    }

    private void EnsureMissing(string path)
    {
        using var existing = _validatedFileAccess.TryOpen(
            path,
            FileAccess.Read,
            ValidatedFileUse.PrePublication);
        if (existing is not null)
        {
            throw new InvalidDataException(
                "A Winora-state restore recovery destination already exists.");
        }
    }

    private static bool PathsEqual(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second));

    private static void EnsureEqual(
        FileSnapshot expected,
        FileSnapshot? actual,
        string message)
    {
        if (actual != expected)
        {
            throw new InvalidDataException(message);
        }
    }

    private sealed record FileSnapshot(
        ValidatedFileIdentity Identity,
        long Length,
        string Sha256)
    {
        internal WinoraStateFileSnapshotDocument ToDocument() =>
            new(
                Identity.VolumeSerialNumber,
                Identity.FileIndex,
                Length,
                Sha256);

        internal static FileSnapshot FromDocument(
            WinoraStateFileSnapshotDocument document) =>
            new(document.Identity, document.Length, document.Sha256);
    }

    private sealed class PreparedRestore
    {
        private readonly SecureOwnedPathLease _parentLease;
        private readonly IAtomicFileOperations _fileOperations;
        private ValidatedFileHandle? _stagingHandle;
        private ValidatedFileHandle? _originalTargetHandle;

        internal PreparedRestore(
            string logicalKey,
            string targetPath,
            string temporaryPath,
            string lastKnownGoodPath,
            FileSnapshot? originalTarget,
            ValidatedFileHandle stagingHandle,
            FileSnapshot staging,
            SecureOwnedPathLease parentLease,
            IAtomicFileOperations fileOperations)
        {
            LogicalKey = logicalKey;
            TargetPath = targetPath;
            TemporaryPath = temporaryPath;
            LastKnownGoodPath = lastKnownGoodPath;
            OriginalTarget = originalTarget;
            _stagingHandle = stagingHandle;
            Staging = staging;
            _parentLease = parentLease;
            _fileOperations = fileOperations;
        }

        internal string LogicalKey { get; }

        internal string TargetPath { get; }

        internal string TemporaryPath { get; }

        internal string LastKnownGoodPath { get; }

        internal FileSnapshot? OriginalTarget { get; }

        internal FileSnapshot Staging { get; }

        internal ValidatedFileHandle StagingHandle => _stagingHandle ??
            throw new InvalidOperationException(
                "The restore staging handle was already released.");

        internal ValidatedFileHandle? OriginalTargetHandle => _originalTargetHandle;

        internal bool HasSystemMutation =>
            _stagingHandle?.HasBeenRenamed == true ||
            _originalTargetHandle?.HasBeenRenamed == true;

        internal bool PublicationAttempted { get; set; }

        internal bool Published { get; set; }

        internal bool RolledBack { get; set; }

        internal void SetOriginalTargetHandle(ValidatedFileHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            if (_originalTargetHandle is not null)
            {
                throw new InvalidOperationException(
                    "The original target handle was already pinned.");
            }

            _originalTargetHandle = handle;
        }

        internal void DeleteStagingHandle()
        {
            var handle = StagingHandle;
            _fileOperations.Delete(handle);
            handle.Dispose();
            _stagingHandle = null;
        }

        internal void DeleteRetainedOriginal()
        {
            if (_originalTargetHandle is null)
            {
                return;
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(_originalTargetHandle.CurrentPath),
                    Path.GetFullPath(LastKnownGoodPath)))
            {
                throw new InvalidDataException(
                    "The retained original target moved before apply cleanup.");
            }

            _fileOperations.Delete(_originalTargetHandle);
            _originalTargetHandle.Dispose();
            _originalTargetHandle = null;
        }

        internal void DeleteRolledBackStaging()
        {
            if (_stagingHandle is null)
            {
                return;
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(_stagingHandle.CurrentPath),
                    Path.GetFullPath(TemporaryPath)))
            {
                throw new InvalidDataException(
                    "The rolled-back staging file moved before cleanup.");
            }

            _fileOperations.Delete(_stagingHandle);
            _stagingHandle.Dispose();
            _stagingHandle = null;
        }

        internal void Dispose(bool safeToCleanRecoveryFiles)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                if ((!Published || RolledBack) && _stagingHandle is not null)
                {
                    try
                    {
                        _fileOperations.Delete(_stagingHandle);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }

                if (safeToCleanRecoveryFiles &&
                    Published &&
                    !RolledBack &&
                    _originalTargetHandle is not null)
                {
                    try
                    {
                        _fileOperations.Delete(_originalTargetHandle);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }
            }
            finally
            {
                _stagingHandle?.Dispose();
                _stagingHandle = null;
                _originalTargetHandle?.Dispose();
                _originalTargetHandle = null;
                _parentLease.Dispose();
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "One or more exact Winora-state restore cleanup operations failed.",
                    cleanupFailures);
            }
        }
    }

    private sealed record RollbackOutcome(
        WinoraStateRestoreRecoveryDocument Recovery,
        IReadOnlyList<Exception> Failures);
}
