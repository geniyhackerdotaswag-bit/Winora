using System.Runtime.ExceptionServices;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Backups;

internal interface IBackupDocumentStore
{
    ValueTask WriteStagingManifestAsync(
        string backupId,
        BackupManifestDocument manifest,
        CancellationToken cancellationToken);

    ValueTask PublishCommittedMarkerAsync(
        string backupId,
        string backupDigest,
        CancellationToken cancellationToken);

    ValueTask<CommittedBackupManifest> ReadCommittedManifestAsync(
        string backupId,
        CancellationToken cancellationToken);
}

internal sealed record BackupDirectoryRenameContext(
    string StagingDirectory,
    string FinalDirectory);

internal sealed record BackupDirectoryDeleteContext(string BackupDirectory);

internal interface IBackupDirectoryRaceHook
{
    void BeforeHandleBoundRename(BackupDirectoryRenameContext context);

    void BeforeVerifiedDeleteOpen(BackupDirectoryDeleteContext context);
}

public sealed class BackupRepository : IBackupRepository
{
    internal const string WinoraStatePlanDigest = "WINORA-STATE-V1";

    private readonly WinoraDataPaths _paths;
    private readonly IBackupCaptureProvider _captureProvider;
    private readonly IBackupDocumentStore _documents;
    private readonly BackupPayloadStore _payloads;
    private readonly AtomicJsonFile _storageTransactions;
    private readonly IBackupDirectoryRaceHook? _directoryRaceHook;

    public BackupRepository(
        WinoraDataPaths paths,
        IBackupCaptureProvider captureProvider,
        TimeProvider? timeProvider = null)
        : this(paths, captureProvider, documents: null, payloads: null, timeProvider)
    {
    }

    internal BackupRepository(
        WinoraDataPaths paths,
        IBackupCaptureProvider captureProvider,
        IBackupDocumentStore? documents,
        BackupPayloadStore? payloads,
        TimeProvider? timeProvider,
        IBackupDirectoryRaceHook? directoryRaceHook = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _captureProvider = captureProvider ?? throw new ArgumentNullException(nameof(captureProvider));
        _documents = documents ?? new AtomicBackupDocumentStore(paths, timeProvider);
        _payloads = payloads ?? new BackupPayloadStore();
        _directoryRaceHook = directoryRaceHook;
        _storageTransactions = new AtomicJsonFile(
            paths,
            serializer: null,
            timeProvider: timeProvider);
    }

    private IBackupDocumentStore Documents => _documents;

    internal ValueTask<IReadOnlyList<BackupStorageCatalogEntry>> ScanStorageCatalogAsync(
        CancellationToken cancellationToken) =>
        _storageTransactions.ExecuteTransactionAsync(
            _ => ScanStorageCatalog(cancellationToken),
            cancellationToken);

    internal ValueTask<bool> DeleteVerifiedBackupAsync(
        BackupStorageCatalogEntry expected,
        bool retentionConfirmedUnreferenced,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!retentionConfirmedUnreferenced ||
            expected.Status != BackupStorageStatus.VerifiedCommitted ||
            !expected.IsVerified)
        {
            throw new InvalidOperationException(
                "Backup deletion requires a verified unreferenced retention decision.");
        }

        return _storageTransactions.ExecuteTransactionAsync(
            _ => DeleteVerifiedBackup(expected, cancellationToken),
            cancellationToken);
    }

    public async ValueTask<BackupReceipt> CreateAndVerifyAsync(
        ChangePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var backupId = plan.PlanId.ToString("N");
        if (Directory.Exists(_paths.GetBackupDirectory(backupId)))
        {
            var existing = await VerifyAsync(backupId, cancellationToken).ConfigureAwait(false);
            return ReceiptFor(existing.Manifest, plan.Digest, BackupCaptureKind.Operation);
        }

        var capture = await _captureProvider.CaptureOperationAsync(
            plan,
            cancellationToken).ConfigureAwait(false);
        ValidateCapture(
            capture,
            BackupCaptureKind.Operation,
            plan.SourceFingerprint);
        var committed = await CommitAsync(
            backupId,
            plan.Digest,
            capture,
            cancellationToken).ConfigureAwait(false);
        return ReceiptFor(committed.Manifest, plan.Digest, BackupCaptureKind.Operation);
    }

    public async ValueTask<BackupReceipt> ReadAndVerifyOperationBackupAsync(
        ChangePlan plan,
        string backupId,
        string backupDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDigest);
        var committed = await VerifyAsync(backupId, cancellationToken).ConfigureAwait(false);
        var receipt = ReceiptFor(
            committed.Manifest,
            plan.Digest,
            BackupCaptureKind.Operation);
        if (!IsExactReceipt(
                receipt,
                backupId,
                backupDigest,
                plan.Digest,
                plan.SourceFingerprint))
        {
            throw new InvalidDataException(
                "The committed backup is not bound to the applying recovery boundary.");
        }

        return receipt;
    }

    public async ValueTask<BackupReceipt> ReadAndVerifyAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var backupId = plan.BackupId;
        var committed = await VerifyAsync(backupId, cancellationToken).ConfigureAwait(false);
        var receipt = ReceiptFor(
            committed.Manifest,
            plan.ChangePlan.Digest,
            BackupCaptureKind.Operation);
        if (!StringComparer.Ordinal.Equals(receipt.BackupId, plan.BackupId) ||
            !StringComparer.Ordinal.Equals(receipt.BackupDigest, plan.BackupDigest) ||
            receipt.CapturedSourceFingerprint != plan.ChangePlan.SourceFingerprint ||
            receipt.LiveSourceFingerprint != plan.ChangePlan.SourceFingerprint)
        {
            throw new InvalidDataException("The committed backup is not bound to the rollback plan.");
        }

        return receipt;
    }

    public async ValueTask<BackupReceipt> CreateRecoveryCheckpointAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var backupId = $"{plan.RollbackId:N}-checkpoint";
        if (Directory.Exists(_paths.GetBackupDirectory(backupId)))
        {
            var existing = await VerifyAsync(backupId, cancellationToken).ConfigureAwait(false);
            return ReceiptFor(
                existing.Manifest,
                plan.Digest,
                BackupCaptureKind.RecoveryCheckpoint);
        }

        var capture = await _captureProvider.CaptureRecoveryCheckpointAsync(
            plan,
            cancellationToken).ConfigureAwait(false);
        ValidateCapture(
            capture,
            BackupCaptureKind.RecoveryCheckpoint,
            plan.AppliedFingerprint);
        var committed = await CommitAsync(
            backupId,
            plan.Digest,
            capture,
            cancellationToken).ConfigureAwait(false);
        return ReceiptFor(
            committed.Manifest,
            plan.Digest,
            BackupCaptureKind.RecoveryCheckpoint);
    }

    public async ValueTask<BackupReceipt> ReadAndVerifyRecoveryCheckpointAsync(
        RollbackPlan plan,
        string checkpointId,
        string checkpointDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointDigest);
        var committed = await VerifyAsync(checkpointId, cancellationToken).ConfigureAwait(false);
        var receipt = ReceiptFor(
            committed.Manifest,
            plan.Digest,
            BackupCaptureKind.RecoveryCheckpoint);
        if (!IsExactReceipt(
                receipt,
                checkpointId,
                checkpointDigest,
                plan.Digest,
                plan.AppliedFingerprint))
        {
            throw new InvalidDataException(
                "The committed recovery checkpoint is not bound to the rolling-back recovery boundary.");
        }

        return receipt;
    }

    internal async ValueTask<WinoraStateBackupReceipt> CreateWinoraStateAsync(
        string backupId,
        BackupCapture capture,
        CancellationToken cancellationToken)
    {
        if (capture.Kind != BackupCaptureKind.WinoraState)
        {
            throw new ArgumentException("The capture is not a Winora-state snapshot.", nameof(capture));
        }

        if (capture.CapturedSourceFingerprint != capture.LiveSourceFingerprint)
        {
            throw new BackupFingerprintMismatchException();
        }

        if (Directory.Exists(_paths.GetBackupDirectory(backupId)))
        {
            var existing = await VerifyAsync(backupId, cancellationToken).ConfigureAwait(false);
            ValidateManifestKindAndPlan(
                existing.Manifest,
                WinoraStatePlanDigest,
                BackupCaptureKind.WinoraState);
            return new WinoraStateBackupReceipt(
                backupId,
                existing.Manifest.BackupDigest,
                true);
        }

        var committed = await CommitAsync(
            backupId,
            WinoraStatePlanDigest,
            capture,
            cancellationToken).ConfigureAwait(false);
        return new WinoraStateBackupReceipt(
            backupId,
            committed.Manifest.BackupDigest,
            true);
    }

    internal async ValueTask<VerifiedBackup> ReadWinoraStateAsync(
        string backupId,
        CancellationToken cancellationToken)
    {
        var committed = await VerifyAsync(backupId, cancellationToken).ConfigureAwait(false);
        ValidateManifestKindAndPlan(
            committed.Manifest,
            WinoraStatePlanDigest,
            BackupCaptureKind.WinoraState);
        return committed;
    }

    private async ValueTask<VerifiedBackup> CommitAsync(
        string backupId,
        string planDigest,
        BackupCapture capture,
        CancellationToken cancellationToken)
    {
        var finalDirectory = _paths.GetBackupDirectory(backupId);
        var stagingDirectory = finalDirectory + ".staging";
        using var backupsLease = SecureOwnedPathLease.Acquire(
            _paths,
            Path.Combine(_paths.BackupsDirectory, ".backup-layout-lease"));
        if (EntryExists(stagingDirectory))
        {
            throw new InvalidDataException(
                "A pre-existing backup staging entry requires explicit recovery review.");
        }

        if (EntryExists(finalDirectory))
        {
            throw new InvalidDataException(
                "A backup directory exists without a verified committed marker.");
        }

        BackupDirectorySecurity.CreateUserOnlyDirectoryNew(stagingDirectory);
        var stagingCreated = true;
        var renamed = false;
        SecureBackupDirectoryLayout.DirectoryIdentity? stagingIdentity = null;
        VerifiedBackup? committed = null;
        Exception? primaryFailure = null;
        try
        {
            BackupManifestDocument manifest;
            using (var stagingLease = SecureBackupDirectoryLayout.AcquirePinnedDirectory(
                       stagingDirectory,
                       allowRename: false))
            {
                stagingIdentity = stagingLease.Identity;
                BackupDirectorySecurity.VerifyUserOnlyDirectory(stagingDirectory);
                var payloadDirectory = Path.Combine(stagingDirectory, "payload");
                BackupDirectorySecurity.CreateUserOnlyDirectoryNew(payloadDirectory);
                using (SecureBackupDirectoryLayout.AcquirePinnedDirectory(
                           payloadDirectory,
                           allowRename: false))
                {
                    BackupDirectorySecurity.VerifyUserOnlyDirectory(payloadDirectory);
                    var artifactDocuments = _payloads.WriteAndVerify(
                        stagingDirectory,
                        capture.Artifacts,
                        cancellationToken);
                    manifest = BackupManifestDocument.Create(
                        backupId,
                        capture.Kind,
                        planDigest,
                        capture.CapturedSourceFingerprint,
                        capture.LiveSourceFingerprint,
                        artifactDocuments);
                }

                await Documents.WriteStagingManifestAsync(
                    backupId,
                    manifest,
                    cancellationToken).ConfigureAwait(false);
                SecureBackupDirectoryLayout.EnsureSingleLinkRegularFile(
                    Path.Combine(stagingDirectory, "manifest.json"));
                if (!stagingLease.MatchesPath(stagingDirectory))
                {
                    throw new IOException(
                        "The backup staging directory identity changed during capture.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var renameLease = SecureBackupDirectoryLayout.AcquirePinnedDirectory(
                       stagingDirectory,
                       allowRename: true))
            {
                if (renameLease.Identity != stagingIdentity)
                {
                    throw new IOException(
                        "The backup staging directory changed before atomic publication.");
                }

                _directoryRaceHook?.BeforeHandleBoundRename(
                    new BackupDirectoryRenameContext(
                        stagingDirectory,
                        finalDirectory));
                renameLease.RenameNew(finalDirectory);
                renamed = true;
                if (!renameLease.MatchesPath(finalDirectory))
                {
                    throw new IOException(
                        "The published backup directory does not match the captured staging identity.");
                }

                using var finalLease = SecureOwnedPathLease.AcquireExistingDirectory(
                    _paths,
                    finalDirectory);
                BackupDirectorySecurity.VerifyUserOnlyDirectory(finalDirectory);
                await Documents.PublishCommittedMarkerAsync(
                    backupId,
                    manifest.BackupDigest,
                    CancellationToken.None).ConfigureAwait(false);
            }

            committed = await VerifyAsync(
                backupId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            if (stagingCreated &&
                !renamed &&
                stagingIdentity is { } expectedIdentity &&
                Directory.Exists(stagingDirectory))
            {
                DeleteOwnedStagingDirectory(stagingDirectory, expectedIdentity);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (primaryFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(
                "Backup publication failed and protected staging cleanup also failed.",
                primaryFailure,
                cleanupFailure);
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        return committed!;
    }

    private async ValueTask<VerifiedBackup> VerifyAsync(
        string backupId,
        CancellationToken cancellationToken)
    {
        var finalDirectory = _paths.GetBackupDirectory(backupId);
        if (!Directory.Exists(finalDirectory))
        {
            throw new InvalidDataException("The committed backup directory is missing.");
        }

        using var directoryLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            finalDirectory);
        BackupDirectorySecurity.VerifyUserOnlyDirectory(finalDirectory);
        var committedManifest = await Documents.ReadCommittedManifestAsync(
            backupId,
            cancellationToken).ConfigureAwait(false);
        var manifest = committedManifest.Manifest;
        manifest.Validate();
        if (!StringComparer.Ordinal.Equals(manifest.BackupId, backupId))
        {
            throw new InvalidDataException("The committed marker and manifest identify another backup.");
        }

        var artifacts = _payloads.ReadAndVerify(
            finalDirectory,
            manifest);
        return new VerifiedBackup(
            manifest,
            artifacts,
            committedManifest.CommittedUtc);
    }

    private static BackupReceipt ReceiptFor(
        BackupManifestDocument manifest,
        string expectedPlanDigest,
        BackupCaptureKind expectedKind)
    {
        ValidateManifestKindAndPlan(manifest, expectedPlanDigest, expectedKind);
        return BackupReceipt.Verified(
            manifest.BackupId,
            manifest.BackupDigest,
            manifest.PlanDigest,
            manifest.CapturedSourceFingerprint,
            manifest.LiveSourceFingerprint);
    }

    private static void ValidateCapture(
        BackupCapture capture,
        BackupCaptureKind expectedKind,
        StateFingerprint expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Kind != expectedKind ||
            capture.CapturedSourceFingerprint != expectedFingerprint ||
            capture.LiveSourceFingerprint != expectedFingerprint)
        {
            throw new BackupFingerprintMismatchException();
        }
    }

    private static bool IsExactReceipt(
        BackupReceipt receipt,
        string backupId,
        string backupDigest,
        string planDigest,
        StateFingerprint fingerprint) =>
        receipt.IsVerified &&
        StringComparer.Ordinal.Equals(receipt.BackupId, backupId) &&
        StringComparer.Ordinal.Equals(receipt.BackupDigest, backupDigest) &&
        StringComparer.Ordinal.Equals(receipt.PlanDigest, planDigest) &&
        receipt.CapturedSourceFingerprint == fingerprint &&
        receipt.LiveSourceFingerprint == fingerprint;

    private static void ValidateManifestKindAndPlan(
        BackupManifestDocument manifest,
        string expectedPlanDigest,
        BackupCaptureKind expectedKind)
    {
        if (manifest.Kind != expectedKind ||
            !StringComparer.Ordinal.Equals(manifest.PlanDigest, expectedPlanDigest))
        {
            throw new InvalidDataException("The committed backup is bound to another plan or backup kind.");
        }
    }

    private void DeleteOwnedStagingDirectory(
        string stagingDirectory,
        SecureBackupDirectoryLayout.DirectoryIdentity expectedIdentity)
    {
        var expectedPrefix = _paths.BackupsDirectory + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(stagingDirectory);
        if (!fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.EndsWith(".staging", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Refusing to delete an unsafe backup staging directory.");
        }

        SecureBackupDirectoryLayout.DeleteTreeWithoutFollowingReparsePoints(
            fullPath,
            expectedIdentity);
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private IReadOnlyList<BackupStorageCatalogEntry> ScanStorageCatalog(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_paths.BackupsDirectory))
        {
            return [];
        }

        using var rootLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            _paths.BackupsDirectory);
        if (Directory.EnumerateFiles(
                _paths.BackupsDirectory,
                "*",
                SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidDataException(
                "The fixed backup store contains an unexpected root file.");
        }

        var catalog = new List<BackupStorageCatalogEntry>();
        foreach (var directory in Directory.EnumerateDirectories(
                     _paths.BackupsDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name.EndsWith(".staging", StringComparison.Ordinal))
            {
                var backupId = name[..^".staging".Length];
                EnsureCanonicalBackupId(backupId);
                catalog.Add(BackupStorageCatalogEntry.RecoveryRequired(
                    backupId,
                    BackupStorageStatus.UncommittedStaging));
                continue;
            }

            EnsureCanonicalBackupId(name);
            try
            {
                var verified = VerifyAsync(name, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                catalog.Add(BackupStorageCatalogEntry.Verified(verified));
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                catalog.Add(BackupStorageCatalogEntry.RecoveryRequired(
                    name,
                    BackupStorageStatus.UnmarkedOrCorruptFinal));
            }
        }

        return Array.AsReadOnly(catalog
            .OrderBy(item => item.BackupId, StringComparer.Ordinal)
            .ToArray());
    }

    private bool DeleteVerifiedBackup(
        BackupStorageCatalogEntry expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = _paths.GetBackupDirectory(expected.BackupId);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var verified = VerifyAsync(expected.BackupId, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        var current = BackupStorageCatalogEntry.Verified(verified);
        if (current != expected)
        {
            throw new InvalidDataException(
                "The backup changed after retention catalog verification.");
        }

        using var rootLease = SecureOwnedPathLease.AcquireExistingDirectory(
            _paths,
            _paths.BackupsDirectory);
        SecureBackupDirectoryLayout.DirectoryIdentity expectedIdentity;
        using (var identityLease = SecureBackupDirectoryLayout.AcquirePinnedDirectory(
                   directory,
                   allowRename: true))
        {
            if (!identityLease.MatchesPath(directory))
            {
                throw new IOException(
                    "The backup directory identity changed before retention deletion.");
            }

            expectedIdentity = identityLease.Identity;
        }

        _directoryRaceHook?.BeforeVerifiedDeleteOpen(
            new BackupDirectoryDeleteContext(directory));
        SecureBackupDirectoryLayout.DeleteTreeWithoutFollowingReparsePoints(
            directory,
            expectedIdentity);
        return true;
    }

    private void EnsureCanonicalBackupId(string backupId)
    {
        try
        {
            var expected = _paths.GetBackupDirectory(backupId);
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetDirectoryName(expected),
                    _paths.BackupsDirectory))
            {
                throw new InvalidDataException(
                    "A backup catalog identifier escaped the fixed root.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The fixed backup store contains an invalid backup identifier.",
                exception);
        }
    }
}

internal sealed record VerifiedBackup(
    BackupManifestDocument Manifest,
    IReadOnlyList<BackupArtifact> Artifacts,
    DateTimeOffset CommittedUtc);

internal enum BackupStorageStatus
{
    VerifiedCommitted,
    UncommittedStaging,
    UnmarkedOrCorruptFinal,
}

internal enum BackupProtectionClass
{
    OperationRollbackSource,
    RecoveryCheckpoint,
    WinoraState,
    RecoveryRequired,
}

internal sealed record BackupStorageCatalogEntry(
    string BackupId,
    BackupStorageStatus Status,
    BackupCaptureKind? Kind,
    string? PlanDigest,
    string? BackupDigest,
    BackupProtectionClass Protection,
    bool IsVerified,
    bool IsRecoveryProtected,
    DateTimeOffset? CommittedUtc)
{
    internal static BackupStorageCatalogEntry Verified(VerifiedBackup backup) =>
        new(
            backup.Manifest.BackupId,
            BackupStorageStatus.VerifiedCommitted,
            backup.Manifest.Kind,
            backup.Manifest.PlanDigest,
            backup.Manifest.BackupDigest,
            backup.Manifest.Kind switch
            {
                BackupCaptureKind.Operation => BackupProtectionClass.OperationRollbackSource,
                BackupCaptureKind.RecoveryCheckpoint => BackupProtectionClass.RecoveryCheckpoint,
                BackupCaptureKind.WinoraState => BackupProtectionClass.WinoraState,
                _ => throw new InvalidDataException("A committed backup has an unknown kind."),
            },
            true,
            true,
            ValidateCommittedUtc(backup.CommittedUtc));

    internal static BackupStorageCatalogEntry RecoveryRequired(
        string backupId,
        BackupStorageStatus status) =>
        new(
            backupId,
            status,
            null,
            null,
            null,
            BackupProtectionClass.RecoveryRequired,
            false,
            true,
            null);

    private static DateTimeOffset ValidateCommittedUtc(DateTimeOffset committedUtc)
    {
        if (committedUtc == default || committedUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "The backup catalog committed timestamp is not authoritative UTC.");
        }

        return committedUtc;
    }

}
