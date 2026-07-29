using System.Diagnostics;
using System.Text.Json.Nodes;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Tests.Operations;
using Winora.Infrastructure.Tests.Persistence;
using Winora.Infrastructure.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Backups;

public sealed class BackupRepositoryTests
{
    private static readonly DateTimeOffset CreatedUtc =
        new(2026, 7, 13, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Verified_backup_is_usable_only_after_committed_marker_publication()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var capture = BackupCapture.ForOperation(
            plan.SourceFingerprint,
            plan.SourceFingerprint,
            [Artifact("registry/value.bin", [1, 2, 3, 4])]);
        var repository = CreateRepository(directory.Path, new QueueCaptureProvider(capture));

        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var backupDirectory = new WinoraDataPaths(directory.Path).GetBackupDirectory(receipt.BackupId);

        Assert.True(receipt.IsVerified);
        Assert.Equal(plan.Digest, receipt.PlanDigest);
        Assert.True(File.Exists(Path.Combine(backupDirectory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(backupDirectory, "manifest.committed.json")));
        Assert.False(Directory.Exists(backupDirectory + ".staging"));
    }

    [Theory]
    [InlineData("sha256", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("SHA-256", "short")]
    [InlineData("SHA-256", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("SHA-256", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEG")]
    public void Backup_manifest_rejects_noncanonical_persisted_fingerprints(
        string algorithm,
        string value)
    {
        var invalid = new StateFingerprint(algorithm, value);
        var logicalKey = "item";
        var artifact = new BackupArtifactDocument(
            logicalKey,
            BackupStorageName.ForLogicalKey(logicalKey),
            "opaque",
            1,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])));

        Assert.Throws<InvalidDataException>(() => BackupManifestDocument.Create(
            "backup",
            BackupCaptureKind.Operation,
            "PLAN-DIGEST",
            invalid,
            TestPlan.Fingerprint("live"),
            [artifact]));
        Assert.Throws<InvalidDataException>(() => BackupManifestDocument.Create(
            "backup",
            BackupCaptureKind.Operation,
            "PLAN-DIGEST",
            TestPlan.Fingerprint("captured"),
            invalid,
            [artifact]));
    }

    [Fact]
    public async Task Directory_without_committed_marker_is_never_accepted_for_rollback()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var provider = new QueueCaptureProvider(ValidCapture(plan));
        var repository = CreateRepository(directory.Path, provider);
        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var backupDirectory = new WinoraDataPaths(directory.Path).GetBackupDirectory(receipt.BackupId);
        File.Delete(Path.Combine(backupDirectory, "manifest.committed.json"));
        var rollback = RollbackPlan.Create(
            Guid.NewGuid(),
            plan,
            receipt,
            TestPlan.Fingerprint("applied"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ReadAndVerifyAsync(rollback, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Rollback_reads_only_the_exact_bound_backup_identifier()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var repository = CreateRepository(
            directory.Path,
            new QueueCaptureProvider(ValidCapture(plan)));
        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var rollback = RollbackPlan.Create(
            Guid.NewGuid(),
            plan,
            receipt with { BackupId = "different-backup" },
            TestPlan.Fingerprint("applied"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ReadAndVerifyAsync(rollback, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Payload_corruption_invalidates_a_committed_backup()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var repository = CreateRepository(directory.Path, new QueueCaptureProvider(ValidCapture(plan)));
        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var backupDirectory = new WinoraDataPaths(directory.Path).GetBackupDirectory(receipt.BackupId);
        await File.WriteAllBytesAsync(
            Path.Combine(
                backupDirectory,
                "payload",
                BackupStorageName.ForLogicalKey("registry/value.bin")),
            [9, 9, 9]);
        var rollback = RollbackPlan.Create(
            Guid.NewGuid(),
            plan,
            receipt,
            TestPlan.Fingerprint("applied"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ReadAndVerifyAsync(rollback, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Committed_manifest_read_blocks_same_path_replacement_after_handle_validation()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var plan = TestPlan.Create();
        var repository = CreateRepository(
            directory.Path,
            new QueueCaptureProvider(ValidCapture(plan)));
        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var backupDirectory = paths.GetBackupDirectory(receipt.BackupId);
        var manifestPath = Path.Combine(backupDirectory, "manifest.json");
        var replacementPath = Path.Combine(backupDirectory, "replacement-manifest.json");
        await File.WriteAllTextAsync(replacementPath, "replacement");
        var observer = new AttemptLeafReplacementObserver(manifestPath, replacementPath);
        var documents = new AtomicBackupDocumentStore(
            paths,
            new FixedTimeProvider(CreatedUtc),
            new WindowsValidatedFileAccess(observer));

        var committed = await documents.ReadCommittedManifestAsync(
            receipt.BackupId,
            CancellationToken.None);

        Assert.IsAssignableFrom<IOException>(observer.ReplacementFailure);
        Assert.Equal(receipt.BackupDigest, committed.Manifest.BackupDigest);
        Assert.False(File.Exists(manifestPath + ".displaced"));
    }

    [Fact]
    public async Task Committed_marker_read_blocks_same_path_replacement_after_handle_validation()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var plan = TestPlan.Create();
        var repository = CreateRepository(
            directory.Path,
            new QueueCaptureProvider(ValidCapture(plan)));
        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var backupDirectory = paths.GetBackupDirectory(receipt.BackupId);
        var markerPath = Path.Combine(backupDirectory, "manifest.committed.json");
        var replacementPath = Path.Combine(backupDirectory, "replacement-marker.json");
        await File.WriteAllTextAsync(replacementPath, "replacement");
        var observer = new AttemptLeafReplacementObserver(markerPath, replacementPath);
        var documents = new AtomicBackupDocumentStore(
            paths,
            new FixedTimeProvider(CreatedUtc),
            validatedFileObserver: observer);

        var committed = await documents.ReadCommittedManifestAsync(
            receipt.BackupId,
            CancellationToken.None);

        Assert.IsAssignableFrom<IOException>(observer.ReplacementFailure);
        Assert.Equal(receipt.BackupDigest, committed.Manifest.BackupDigest);
        Assert.False(File.Exists(markerPath + ".displaced"));
    }

    [Fact]
    public async Task Committed_payload_read_blocks_same_path_replacement_after_handle_validation()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var plan = TestPlan.Create();
        var repository = CreateRepository(
            directory.Path,
            new QueueCaptureProvider(ValidCapture(plan)));
        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var backupDirectory = paths.GetBackupDirectory(receipt.BackupId);
        var documents = new AtomicBackupDocumentStore(
            paths,
            new FixedTimeProvider(CreatedUtc));
        var committed = await documents.ReadCommittedManifestAsync(
            receipt.BackupId,
            CancellationToken.None);
        var payloadPath = Path.Combine(
            backupDirectory,
            "payload",
            BackupStorageName.ForLogicalKey("registry/value.bin"));
        var replacementPath = Path.Combine(
            backupDirectory,
            "payload",
            "replacement.bin");
        await File.WriteAllBytesAsync(replacementPath, [9, 9, 9]);
        var observer = new AttemptLeafReplacementObserver(payloadPath, replacementPath);
        var payloads = new BackupPayloadStore(
            validatedFileAccess: new WindowsValidatedFileAccess(observer));

        var artifacts = payloads.ReadAndVerify(backupDirectory, committed.Manifest);

        Assert.IsAssignableFrom<IOException>(observer.ReplacementFailure);
        Assert.Equal([1, 2, 3, 4], Assert.Single(artifacts).Content.ToArray());
        Assert.False(File.Exists(payloadPath + ".displaced"));
    }

    [Fact]
    public async Task Committed_payload_with_a_hard_link_alias_is_rejected()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var repository = CreateRepository(
            directory.Path,
            new QueueCaptureProvider(ValidCapture(plan)));
        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var backupDirectory = new WinoraDataPaths(directory.Path).GetBackupDirectory(
            receipt.BackupId);
        var payload = Path.Combine(
            backupDirectory,
            "payload",
            BackupStorageName.ForLogicalKey("registry/value.bin"));
        Assert.True(HardLinkNativeMethods.TryCreate(payload + ".alias", payload));
        var rollback = RollbackPlan.Create(
            Guid.NewGuid(),
            plan,
            receipt,
            TestPlan.Fingerprint("applied"));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            repository.ReadAndVerifyAsync(
                rollback,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Capture_drift_discards_staging_and_never_publishes_backup()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var capture = BackupCapture.ForOperation(
            TestPlan.Fingerprint("external"),
            plan.SourceFingerprint,
            [Artifact("registry/value.bin", [1])]);
        var repository = CreateRepository(directory.Path, new QueueCaptureProvider(capture));

        await Assert.ThrowsAsync<BackupFingerprintMismatchException>(() =>
            repository.CreateAndVerifyAsync(plan, CancellationToken.None).AsTask());

        var backupDirectory = new WinoraDataPaths(directory.Path)
            .GetBackupDirectory(plan.PlanId.ToString("N"));
        Assert.False(Directory.Exists(backupDirectory));
        Assert.False(Directory.Exists(backupDirectory + ".staging"));
    }

    [Fact]
    public async Task Rollback_checkpoint_rejects_capture_race_even_when_live_value_matches_again()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var applied = TestPlan.Fingerprint("applied");
        var rollback = RollbackPlan.Create(
            Guid.NewGuid(),
            plan,
            BackupReceipt.Verified(
                plan.PlanId.ToString("N"),
                "ORIGINAL-BACKUP",
                plan.Digest,
                plan.SourceFingerprint,
                plan.SourceFingerprint),
            applied);
        var racedCapture = BackupCapture.ForRecoveryCheckpoint(
            TestPlan.Fingerprint("transient-external"),
            applied,
            [Artifact("checkpoint/value.bin", [7])]);
        var repository = CreateRepository(directory.Path, new QueueCaptureProvider(racedCapture));

        await Assert.ThrowsAsync<BackupFingerprintMismatchException>(() =>
            repository.CreateRecoveryCheckpointAsync(rollback, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Repeated_checkpoint_creation_reuses_the_verified_committed_checkpoint()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var applied = TestPlan.Fingerprint("applied");
        var rollback = RollbackPlan.Create(
            Guid.NewGuid(),
            plan,
            BackupReceipt.Verified(
                plan.PlanId.ToString("N"),
                "ORIGINAL-BACKUP",
                plan.Digest,
                plan.SourceFingerprint,
                plan.SourceFingerprint),
            applied);
        var provider = new QueueCaptureProvider(BackupCapture.ForRecoveryCheckpoint(
            applied,
            applied,
            [Artifact("checkpoint/value.bin", [7])]));
        var repository = CreateRepository(directory.Path, provider);

        var first = await repository.CreateRecoveryCheckpointAsync(rollback, CancellationToken.None);
        var second = await repository.CreateRecoveryCheckpointAsync(rollback, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, provider.CheckpointCaptureCount);
    }

    [Fact]
    public async Task Logical_keys_are_hash_encoded_instead_of_used_as_windows_paths()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var capture = BackupCapture.ForOperation(
            plan.SourceFingerprint,
            plan.SourceFingerprint,
            [
                Artifact("con", [1]),
                Artifact("a/com1", [2]),
            ]);
        var repository = CreateRepository(directory.Path, new QueueCaptureProvider(capture));

        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);

        var backupDirectory = new WinoraDataPaths(directory.Path).GetBackupDirectory(receipt.BackupId);
        var payloadFiles = Directory.EnumerateFiles(
                Path.Combine(backupDirectory, "payload"),
                "*",
                SearchOption.AllDirectories)
            .ToArray();
        Assert.Equal(2, payloadFiles.Length);
        Assert.All(payloadFiles, path =>
        {
            Assert.Equal(Path.Combine(backupDirectory, "payload"), Path.GetDirectoryName(path));
            Assert.Matches("^[0-9a-f]{64}\\.bin$", Path.GetFileName(path));
        });

        var serializer = new JsonDocumentSerializer();
        var manifest = serializer.DeserializeAndValidate<BackupManifestDocument>(
            await File.ReadAllBytesAsync(Path.Combine(backupDirectory, "manifest.json")));
        Assert.Equal(
            ["a/com1", "con"],
            manifest.Payload.Artifacts.Select(artifact => artifact.LogicalKey).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Junction_created_after_initial_check_is_rejected_without_writing_outside(
        bool staging)
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var plan = TestPlan.Create();
        var target = paths.GetBackupDirectory(plan.PlanId.ToString("N")) +
            (staging ? ".staging" : string.Empty);
        var provider = new QueueCaptureProvider(ValidCapture(plan))
        {
            BeforeOperationCapture = () =>
            {
                Directory.CreateDirectory(paths.BackupsDirectory);
                CreateJunction(target, outside.Path);
            },
        };
        var repository = CreateRepository(directory.Path, provider);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                repository.CreateAndVerifyAsync(plan, CancellationToken.None).AsTask());
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target);
            }
        }
    }

    [Fact]
    public async Task Backup_catalog_exposes_rollback_protection_and_deletes_only_after_retention_authorization()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var repository = CreateRepository(
            directory.Path,
            new QueueCaptureProvider(ValidCapture(plan)));
        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);

        var catalog = await repository.ScanStorageCatalogAsync(CancellationToken.None);

        var entry = Assert.Single(catalog);
        Assert.Equal(receipt.BackupId, entry.BackupId);
        Assert.Equal(BackupStorageStatus.VerifiedCommitted, entry.Status);
        Assert.Equal(BackupProtectionClass.OperationRollbackSource, entry.Protection);
        Assert.True(entry.IsRecoveryProtected);
        Assert.Equal(CreatedUtc, entry.CommittedUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteVerifiedBackupAsync(
                entry,
                retentionConfirmedUnreferenced: false,
                CancellationToken.None).AsTask());

        var deleted = await repository.DeleteVerifiedBackupAsync(
            entry,
            retentionConfirmedUnreferenced: true,
            CancellationToken.None);

        Assert.True(deleted);
        Assert.False(Directory.Exists(
            new WinoraDataPaths(directory.Path).GetBackupDirectory(receipt.BackupId)));
    }

    [Fact]
    public async Task Tampered_hash_bound_committed_timestamp_fails_closed_as_recovery_required()
    {
        using var directory = new TemporaryDirectory();
        var plan = TestPlan.Create();
        var paths = new WinoraDataPaths(directory.Path);
        var repository = CreateRepository(
            directory.Path,
            new QueueCaptureProvider(ValidCapture(plan)));
        var receipt = await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var markerPath = Path.Combine(
            paths.GetBackupDirectory(receipt.BackupId),
            "manifest.committed.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(markerPath)) ??
            throw new InvalidDataException("The committed marker test fixture is empty.");
        document["payload"]!["committedUtc"] = CreatedUtc.AddDays(-100).ToString("O");
        await File.WriteAllTextAsync(markerPath, document.ToJsonString());

        var entry = Assert.Single(await repository.ScanStorageCatalogAsync(
            CancellationToken.None));

        Assert.Equal(BackupStorageStatus.UnmarkedOrCorruptFinal, entry.Status);
        Assert.Equal(BackupProtectionClass.RecoveryRequired, entry.Protection);
        Assert.True(entry.IsRecoveryProtected);
        Assert.Null(entry.CommittedUtc);
    }

    [Fact]
    public void Safe_cleanup_rejects_a_child_swapped_to_a_junction_without_touching_target()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "owned-staging");
        var child = Path.Combine(root, "payload");
        var original = Path.Combine(root, "payload-original");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "inside.bin"), "inside");
        var outsideFile = Path.Combine(outside.Path, "must-survive.bin");
        File.WriteAllText(outsideFile, "outside");
        var swapped = false;

        try
        {
            Assert.ThrowsAny<IOException>(() =>
                SecureBackupDirectoryLayout.DeleteTreeWithoutFollowingReparsePoints(
                    root,
                    entry =>
                    {
                        if (swapped ||
                            !StringComparer.OrdinalIgnoreCase.Equals(entry, child))
                        {
                            return;
                        }

                        Directory.Move(child, original);
                        CreateJunction(child, outside.Path);
                        swapped = true;
                    }));
            Assert.True(swapped);
            Assert.Equal("outside", File.ReadAllText(outsideFile));
        }
        finally
        {
            if (swapped && Directory.Exists(child))
            {
                Directory.Delete(child);
            }
        }
    }

    [Fact]
    public async Task Publication_renames_the_pinned_staging_directory_not_a_swapped_path()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var plan = TestPlan.Create();
        var hook = new SwapBackupDirectoryRaceHook
        {
            BeforeRename = context =>
            {
                Directory.Move(
                    context.StagingDirectory,
                    context.StagingDirectory + "-displaced");
                Directory.CreateDirectory(context.StagingDirectory);
                File.WriteAllText(
                    Path.Combine(context.StagingDirectory, "attacker-sentinel"),
                    "must-not-publish");
            },
        };
        var repository = new BackupRepository(
            paths,
            new QueueCaptureProvider(ValidCapture(plan)),
            documents: null,
            payloads: null,
            new FixedTimeProvider(CreatedUtc),
            hook);

        var receipt = await repository.CreateAndVerifyAsync(
            plan,
            CancellationToken.None);

        Assert.True(receipt.IsVerified);
        Assert.True(File.Exists(Path.Combine(
            paths.GetBackupDirectory(receipt.BackupId),
            "manifest.committed.json")));
        Assert.Equal(
            "must-not-publish",
            File.ReadAllText(Path.Combine(
                paths.GetBackupDirectory(receipt.BackupId) + ".staging",
                "attacker-sentinel")));
    }

    [Fact]
    public async Task Retention_delete_rejects_a_root_swapped_after_catalog_verification()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var plan = TestPlan.Create();
        string? displaced = null;
        var hook = new SwapBackupDirectoryRaceHook
        {
            BeforeDeleteOpen = context =>
            {
                displaced = context.BackupDirectory + "-displaced";
                Directory.Move(context.BackupDirectory, displaced);
                Directory.CreateDirectory(context.BackupDirectory);
                File.WriteAllText(
                    Path.Combine(context.BackupDirectory, "replacement-sentinel"),
                    "must-survive");
            },
        };
        var repository = new BackupRepository(
            paths,
            new QueueCaptureProvider(ValidCapture(plan)),
            documents: null,
            payloads: null,
            new FixedTimeProvider(CreatedUtc),
            hook);
        await repository.CreateAndVerifyAsync(plan, CancellationToken.None);
        var entry = Assert.Single(await repository.ScanStorageCatalogAsync(
            CancellationToken.None));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            repository.DeleteVerifiedBackupAsync(
                entry,
                retentionConfirmedUnreferenced: true,
                CancellationToken.None).AsTask());

        Assert.NotNull(displaced);
        Assert.True(Directory.Exists(displaced));
        Assert.Equal(
            "must-survive",
            File.ReadAllText(Path.Combine(
                paths.GetBackupDirectory(entry.BackupId),
                "replacement-sentinel")));
    }

    [Fact]
    public async Task Failed_publication_cleanup_never_deletes_a_swapped_staging_root()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var plan = TestPlan.Create();
        string? displaced = null;
        var hook = new SwapBackupDirectoryRaceHook
        {
            BeforeRename = context =>
            {
                displaced = context.StagingDirectory + "-displaced";
                Directory.Move(context.StagingDirectory, displaced);
                Directory.CreateDirectory(context.StagingDirectory);
                File.WriteAllText(
                    Path.Combine(context.StagingDirectory, "replacement-sentinel"),
                    "must-survive-cleanup");
                Directory.CreateDirectory(context.FinalDirectory);
                File.WriteAllText(
                    Path.Combine(context.FinalDirectory, "final-sentinel"),
                    "blocks-create-new-rename");
            },
        };
        var repository = new BackupRepository(
            paths,
            new QueueCaptureProvider(ValidCapture(plan)),
            documents: null,
            payloads: null,
            new FixedTimeProvider(CreatedUtc),
            hook);

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            repository.CreateAndVerifyAsync(plan, CancellationToken.None).AsTask());

        Assert.IsAssignableFrom<IOException>(failure.InnerExceptions[0]);
        Assert.IsType<InvalidDataException>(failure.InnerExceptions[1]);
        Assert.NotNull(displaced);
        Assert.True(File.Exists(Path.Combine(displaced, "manifest.json")));
        Assert.Equal(
            "must-survive-cleanup",
            File.ReadAllText(Path.Combine(
                paths.GetBackupDirectory(plan.PlanId.ToString("N")) + ".staging",
                "replacement-sentinel")));
    }

    private static BackupCapture ValidCapture(ChangePlan plan) =>
        BackupCapture.ForOperation(
            plan.SourceFingerprint,
            plan.SourceFingerprint,
            [Artifact("registry/value.bin", [1, 2, 3, 4])]);

    private static BackupArtifact Artifact(string key, byte[] content) =>
        BackupArtifact.Create(key, "opaque", content);

    private static BackupRepository CreateRepository(string root, IBackupCaptureProvider provider) =>
        new(
            new WinoraDataPaths(root),
            provider,
            new FixedTimeProvider(CreatedUtc));

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Unable to start junction helper.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }
}

internal sealed class SwapBackupDirectoryRaceHook : IBackupDirectoryRaceHook
{
    internal Action<BackupDirectoryRenameContext>? BeforeRename { get; init; }

    internal Action<BackupDirectoryDeleteContext>? BeforeDeleteOpen { get; init; }

    public void BeforeHandleBoundRename(BackupDirectoryRenameContext context) =>
        BeforeRename?.Invoke(context);

    public void BeforeVerifiedDeleteOpen(BackupDirectoryDeleteContext context) =>
        BeforeDeleteOpen?.Invoke(context);
}

internal sealed class QueueCaptureProvider(params BackupCapture[] captures) : IBackupCaptureProvider
{
    private readonly Queue<BackupCapture> _captures = new(captures);

    internal int CheckpointCaptureCount { get; private set; }

    internal Action? BeforeOperationCapture { get; init; }

    public ValueTask<BackupCapture> CaptureOperationAsync(
        ChangePlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeOperationCapture?.Invoke();
        return ValueTask.FromResult(_captures.Dequeue());
    }

    public ValueTask<BackupCapture> CaptureRecoveryCheckpointAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckpointCaptureCount++;
        return ValueTask.FromResult(_captures.Dequeue());
    }
}
