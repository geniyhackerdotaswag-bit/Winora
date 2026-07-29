using System.Diagnostics;
using Winora.Core.Contracts;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.Tests.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Backups;

public sealed class WinoraStateBackupServiceTests
{
    [Fact]
    public async Task Manual_state_backup_captures_only_data_and_assets_and_restores_idempotently()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Write(paths.DataDirectory, "app-settings.json", "settings-before");
        Write(paths.AssetsDirectory, "icons/app.ico", "asset-before");
        Write(paths.OperationsDirectory, "operation/secret.json", "operation-before");
        Write(paths.JournalDirectory, "Events/event.json", "journal-before");
        Write(paths.PendingDirectory, "rendezvous.json", "pending-before");
        var service = new WinoraStateBackupService(paths);

        var receipt = await service.CreateAsync("manual-state", CancellationToken.None);
        Write(paths.DataDirectory, "app-settings.json", "settings-after");
        Write(paths.AssetsDirectory, "icons/app.ico", "asset-after");
        Write(paths.OperationsDirectory, "operation/secret.json", "operation-after");
        Write(paths.JournalDirectory, "Events/event.json", "journal-after");
        Write(paths.PendingDirectory, "rendezvous.json", "pending-after");

        var verified = await service.VerifyAsync(receipt.BackupId, CancellationToken.None);
        await service.RestoreAsync(receipt.BackupId, CancellationToken.None);
        await service.RestoreAsync(receipt.BackupId, CancellationToken.None);

        Assert.True(verified.IsVerified);
        Assert.Equal("settings-before", Read(paths.DataDirectory, "app-settings.json"));
        Assert.Equal("asset-before", Read(paths.AssetsDirectory, "icons/app.ico"));
        Assert.Equal("operation-after", Read(paths.OperationsDirectory, "operation/secret.json"));
        Assert.Equal("journal-after", Read(paths.JournalDirectory, "Events/event.json"));
        Assert.Equal("pending-after", Read(paths.PendingDirectory, "rendezvous.json"));
    }

    [Fact]
    public async Task Manual_state_restore_rejects_corrupt_payload_before_changing_live_state()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Write(paths.DataDirectory, "app-settings.json", "settings-before");
        var service = new WinoraStateBackupService(paths);
        var receipt = await service.CreateAsync("manual-state", CancellationToken.None);
        Write(paths.DataDirectory, "app-settings.json", "settings-live");
        var payload = Path.Combine(
            paths.GetBackupDirectory(receipt.BackupId),
            "payload",
            BackupStorageName.ForLogicalKey("data/app-settings.json"));
        await File.WriteAllTextAsync(payload, "corrupt");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreAsync(receipt.BackupId, CancellationToken.None).AsTask());

        Assert.Equal("settings-live", Read(paths.DataDirectory, "app-settings.json"));
    }

    [Fact]
    public void State_restore_rejects_windows_reserved_logical_names_before_any_write()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var restorer = new WinoraStateRestorer(paths);

        Assert.Throws<InvalidDataException>(() =>
            restorer.Restore(
                [BackupArtifact.Create("data/con", "winora-state-file", new byte[] { 1 })],
                CancellationToken.None));
        Assert.False(Directory.Exists(paths.DataDirectory));
    }

    [Fact]
    public void State_capture_rejects_a_reparse_child_instead_of_silently_omitting_it()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(Path.Combine(outside.Path, "outside.json"), "outside");
        var junction = Path.Combine(paths.DataDirectory, "linked");
        CreateJunction(junction, outside.Path);

        try
        {
            Assert.ThrowsAny<IOException>(() =>
                new WinoraStateSnapshotCapture(paths).Capture(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void State_capture_reads_a_leaf_through_the_validated_handle_that_blocks_replacement()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var target = Path.Combine(paths.DataDirectory, "app-settings.json");
        var replacement = Path.Combine(paths.DataDirectory, "replacement.json");
        Write(paths.DataDirectory, "app-settings.json", "captured-value");
        Write(paths.DataDirectory, "replacement.json", "external-value");
        var observer = new AttemptLeafReplacementObserver(target, replacement);
        var capture = new WinoraStateSnapshotCapture(
            paths,
            new WindowsValidatedFileAccess(observer));

        var snapshot = capture.Capture(CancellationToken.None);

        Assert.IsAssignableFrom<IOException>(observer.ReplacementFailure);
        Assert.Equal("captured-value", Read(paths.DataDirectory, "app-settings.json"));
        Assert.Contains(snapshot.Artifacts, artifact =>
            artifact.Key == "data/app-settings.json" &&
            System.Text.Encoding.UTF8.GetString(artifact.Content.Span) == "captured-value");
    }

    [Fact]
    public void State_restore_revalidates_a_target_replaced_after_preflight_and_does_not_overwrite_it()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        var target = Path.Combine(paths.DataDirectory, "app-settings.json");
        var replacement = Path.Combine(paths.DataDirectory, "replacement.json");
        var displaced = Path.Combine(paths.DataDirectory, "displaced.json");
        Write(paths.DataDirectory, "app-settings.json", "live-value");
        Write(paths.DataDirectory, "replacement.json", "external-value");
        var hook = new ReplaceTargetAfterPreflightHook(target, replacement, displaced);
        var restorer = new WinoraStateRestorer(paths, publicationRaceHook: hook);

        Assert.Throws<InvalidDataException>(() =>
            restorer.Restore(
                [BackupArtifact.Create(
                    "data/app-settings.json",
                    "winora-state-file",
                    System.Text.Encoding.UTF8.GetBytes("backup-value"))],
                CancellationToken.None));

        Assert.True(hook.WasInvoked);
        Assert.Equal("external-value", File.ReadAllText(target));
        Assert.Equal("live-value", File.ReadAllText(displaced));
    }

    [Fact]
    public void State_restore_rejects_a_junction_parent_without_touching_its_target()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Directory.CreateDirectory(paths.DataDirectory);
        var outsideFile = Path.Combine(outside.Path, "settings.json");
        File.WriteAllText(outsideFile, "outside");
        var junction = Path.Combine(paths.DataDirectory, "linked");
        CreateJunction(junction, outside.Path);

        try
        {
            var restorer = new WinoraStateRestorer(paths);

            Assert.ThrowsAny<IOException>(() =>
                restorer.Restore(
                    [BackupArtifact.Create(
                        "data/linked/settings.json",
                        "winora-state-file",
                        System.Text.Encoding.UTF8.GetBytes("backup"))],
                    CancellationToken.None));
            Assert.Equal("outside", File.ReadAllText(outsideFile));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void Failed_compensating_rollback_is_reported_and_preserves_the_original_lkg()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Write(paths.DataDirectory, "first.json", "first-live");
        Write(paths.DataDirectory, "second.json", "second-live");
        var operations = new FailSecondApplyAndRollbackOperations();
        var restorer = new WinoraStateRestorer(paths, fileOperations: operations);

        var failure = Assert.Throws<AggregateException>(() =>
            restorer.Restore(
                [
                    BackupArtifact.Create(
                        "data/first.json",
                        "winora-state-file",
                        System.Text.Encoding.UTF8.GetBytes("first-backup")),
                    BackupArtifact.Create(
                        "data/second.json",
                        "winora-state-file",
                        System.Text.Encoding.UTF8.GetBytes("second-backup")),
                ],
                CancellationToken.None));

        Assert.IsType<InjectedSecondApplyFailure>(failure.InnerExceptions[0]);
        Assert.Contains(
            failure.InnerExceptions,
            exception => exception is InjectedCompensatingRollbackFailure);
        Assert.Single(Directory.EnumerateFiles(
            paths.DataDirectory,
            "first.json.*.restore.lkg"));
    }

    [Fact]
    public async Task Crash_after_file_publication_is_discovered_and_recovered_after_restart()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Write(paths.DataDirectory, "app-settings.json", "live-before-crash");
        var ready = Path.Combine(directory.Path, "crashed-after-publication");
        using var process = StartStateRestoreCrashProcess(directory.Path, ready);

        await process.WaitForExitAsync();

        Assert.Equal(86, process.ExitCode);
        Assert.True(File.Exists(ready));
        Assert.Equal("backup-from-crashed-process", Read(
            paths.DataDirectory,
            "app-settings.json"));
        var restarted = new WinoraStateBackupService(paths);
        var pending = await restarted.InspectPendingRestoreAsync(CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(WinoraStateRestoreRecoveryStatus.Applying, pending.Status);

        await restarted.RecoverPendingRestoreAsync(CancellationToken.None);

        Assert.Equal("live-before-crash", Read(paths.DataDirectory, "app-settings.json"));
        Assert.Null(await restarted.InspectPendingRestoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Crash_after_first_forward_rename_recovers_the_missing_target_topology()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Write(paths.DataDirectory, "app-settings.json", "live-before-crash");
        var ready = Path.Combine(directory.Path, "crashed-after-first-rename");
        using var process = StartStateRestoreCrashProcess(
            directory.Path,
            ready,
            "state-restore-crash-after-first-rename");

        await process.WaitForExitAsync();

        Assert.Equal(87, process.ExitCode);
        Assert.True(File.Exists(ready));
        Assert.False(File.Exists(Path.Combine(paths.DataDirectory, "app-settings.json")));
        Assert.Single(Directory.EnumerateFiles(
            paths.DataDirectory,
            "app-settings.json.*.restore.lkg"));
        var restarted = new WinoraStateBackupService(paths);
        Assert.NotNull(await restarted.InspectPendingRestoreAsync(CancellationToken.None));

        await restarted.RecoverPendingRestoreAsync(CancellationToken.None);

        Assert.Equal("live-before-crash", Read(paths.DataDirectory, "app-settings.json"));
        Assert.Empty(Directory.EnumerateFiles(
            paths.DataDirectory,
            "app-settings.json.*.restore.*"));
        Assert.Null(await restarted.InspectPendingRestoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Apply_cleanup_failure_remains_durable_and_is_recovered_after_restart()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Write(paths.DataDirectory, "app-settings.json", "live-before-restore");
        var cleanupFailure = new FailFirstMatchingCleanupOperations(".restore.lkg");
        var restorer = new WinoraStateRestorer(paths, fileOperations: cleanupFailure);

        Assert.ThrowsAny<Exception>(() =>
            restorer.Restore(
                [BackupArtifact.Create(
                    "data/app-settings.json",
                    "winora-state-file",
                    System.Text.Encoding.UTF8.GetBytes("restored-value"))],
                CancellationToken.None));

        Assert.Equal("restored-value", Read(paths.DataDirectory, "app-settings.json"));
        var restarted = new WinoraStateBackupService(paths);
        var pending = await restarted.InspectPendingRestoreAsync(CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(WinoraStateRestoreRecoveryStatus.CleanupAfterApply, pending.Status);

        await restarted.RecoverPendingRestoreAsync(CancellationToken.None);

        Assert.Equal("restored-value", Read(paths.DataDirectory, "app-settings.json"));
        Assert.Empty(Directory.EnumerateFiles(
            paths.DataDirectory,
            "app-settings.json.*.restore.lkg"));
        Assert.Null(await restarted.InspectPendingRestoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_cleanup_failure_remains_durable_and_is_recovered_after_restart()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Write(paths.DataDirectory, "app-settings.json", "live-before-restore");
        var cleanupFailure = new FailFirstMatchingCleanupOperations(".restore.tmp");
        var restorer = new WinoraStateRestorer(
            paths,
            fileOperations: cleanupFailure,
            publicationRaceHook: new ThrowAfterPublicationHook());

        Assert.ThrowsAny<Exception>(() =>
            restorer.Restore(
                [BackupArtifact.Create(
                    "data/app-settings.json",
                    "winora-state-file",
                    System.Text.Encoding.UTF8.GetBytes("restored-value"))],
                CancellationToken.None));

        Assert.Equal("live-before-restore", Read(paths.DataDirectory, "app-settings.json"));
        var restarted = new WinoraStateBackupService(paths);
        var pending = await restarted.InspectPendingRestoreAsync(CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(WinoraStateRestoreRecoveryStatus.CleanupAfterRollback, pending.Status);

        await restarted.RecoverPendingRestoreAsync(CancellationToken.None);

        Assert.Equal("live-before-restore", Read(paths.DataDirectory, "app-settings.json"));
        Assert.Empty(Directory.EnumerateFiles(
            paths.DataDirectory,
            "app-settings.json.*.restore.tmp"));
        Assert.Null(await restarted.InspectPendingRestoreAsync(CancellationToken.None));
    }

    [Fact]
    public void Recovery_document_rejects_original_and_staging_roles_with_the_same_file_identity()
    {
        var original = RestoreSnapshot(fileIndex: 1, hashCharacter: 'A');
        var staging = RestoreSnapshot(fileIndex: 1, hashCharacter: 'B');
        var document = RestoreDocument(
            WinoraStateRestoreRecoveryStatus.Prepared,
            RestoreEntry(
                "data/app-settings.json",
                nonceCharacter: '0',
                original,
                staging,
                WinoraStateRestoreEntryStatus.Prepared));

        Assert.Throws<InvalidDataException>(document.Validate);
    }

    [Fact]
    public void Recovery_document_rejects_case_aliases_for_the_same_windows_target()
    {
        var document = RestoreDocument(
            WinoraStateRestoreRecoveryStatus.Prepared,
            RestoreEntry(
                "data/Foo.json",
                nonceCharacter: '0',
                original: null,
                RestoreSnapshot(fileIndex: 1, hashCharacter: 'A'),
                WinoraStateRestoreEntryStatus.Prepared),
            RestoreEntry(
                "data/foo.json",
                nonceCharacter: '1',
                original: null,
                RestoreSnapshot(fileIndex: 2, hashCharacter: 'B'),
                WinoraStateRestoreEntryStatus.Prepared));

        Assert.Throws<InvalidDataException>(document.Validate);
    }

    [Fact]
    public void Recovery_document_rejects_noncausal_rolling_back_progress()
    {
        var document = RestoreDocument(
            WinoraStateRestoreRecoveryStatus.RollingBack,
            RestoreEntry(
                "data/first.json",
                nonceCharacter: '0',
                original: null,
                RestoreSnapshot(fileIndex: 1, hashCharacter: 'A'),
                WinoraStateRestoreEntryStatus.Prepared),
            RestoreEntry(
                "data/second.json",
                nonceCharacter: '1',
                original: null,
                RestoreSnapshot(fileIndex: 2, hashCharacter: 'B'),
                WinoraStateRestoreEntryStatus.Applied),
            RestoreEntry(
                "data/third.json",
                nonceCharacter: '2',
                original: null,
                RestoreSnapshot(fileIndex: 3, hashCharacter: 'C'),
                WinoraStateRestoreEntryStatus.RolledBack));

        Assert.Throws<InvalidDataException>(document.Validate);
    }

    [Fact]
    public void Recovery_document_rejects_noncausal_recovery_required_progress()
    {
        var document = RestoreDocument(
            WinoraStateRestoreRecoveryStatus.RecoveryRequired,
            RestoreEntry(
                "data/first.json",
                nonceCharacter: '0',
                original: null,
                RestoreSnapshot(fileIndex: 1, hashCharacter: 'A'),
                WinoraStateRestoreEntryStatus.Prepared),
            RestoreEntry(
                "data/second.json",
                nonceCharacter: '1',
                original: null,
                RestoreSnapshot(fileIndex: 2, hashCharacter: 'B'),
                WinoraStateRestoreEntryStatus.Applied));

        Assert.Throws<InvalidDataException>(document.Validate);
    }

    [Theory]
    [InlineData("other.json")]
    [InlineData("CON")]
    [InlineData("app-settings.json.0123456789abcdef0123456789abcdef.restore.wrong")]
    public async Task Recovery_rejects_a_tampered_staging_leaf_not_bound_to_the_target(
        string tamperedLeaf)
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Directory.CreateDirectory(paths.PendingDirectory);
        var original = new WinoraStateFileSnapshotDocument(
            1,
            1,
            1,
            new string('A', 64));
        var staging = new WinoraStateFileSnapshotDocument(
            1,
            2,
            1,
            new string('B', 64));
        var document = new WinoraStateRestoreRecoveryDocument(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            WinoraStateRestoreRecoveryStatus.Applying,
            [
                new WinoraStateRestoreEntryDocument(
                    "data/app-settings.json",
                    tamperedLeaf,
                    "app-settings.json.0123456789abcdef0123456789abcdef.restore.lkg",
                    original,
                    staging,
                    WinoraStateRestoreEntryStatus.Applying),
            ]);
        var serializer = new JsonDocumentSerializer();
        await File.WriteAllBytesAsync(
            paths.WinoraStateRestoreRecoveryFile,
            serializer.Serialize(serializer.CreateEnvelope(
                "winora-state-restore-recovery",
                new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
                document)));

        var service = new WinoraStateBackupService(paths);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InspectPendingRestoreAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(WinoraStateRestoreRecoveryStatus.Prepared, WinoraStateRestoreEntryStatus.Applied)]
    [InlineData(WinoraStateRestoreRecoveryStatus.Applying, WinoraStateRestoreEntryStatus.Prepared)]
    [InlineData(WinoraStateRestoreRecoveryStatus.RollingBack, WinoraStateRestoreEntryStatus.Prepared)]
    [InlineData(WinoraStateRestoreRecoveryStatus.CleanupAfterApply, WinoraStateRestoreEntryStatus.RolledBack)]
    [InlineData(WinoraStateRestoreRecoveryStatus.CleanupAfterRollback, WinoraStateRestoreEntryStatus.Applied)]
    [InlineData(WinoraStateRestoreRecoveryStatus.RecoveryRequired, WinoraStateRestoreEntryStatus.RolledBack)]
    [InlineData(WinoraStateRestoreRecoveryStatus.Completed, WinoraStateRestoreEntryStatus.Prepared)]
    [InlineData(WinoraStateRestoreRecoveryStatus.RolledBack, WinoraStateRestoreEntryStatus.Applied)]
    internal async Task Recovery_rejects_tampered_cross_field_status_and_keeps_record_visible(
        WinoraStateRestoreRecoveryStatus documentStatus,
        WinoraStateRestoreEntryStatus entryStatus)
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Directory.CreateDirectory(paths.PendingDirectory);
        var original = new WinoraStateFileSnapshotDocument(
            1,
            1,
            1,
            new string('A', 64));
        var staging = new WinoraStateFileSnapshotDocument(
            1,
            2,
            1,
            new string('B', 64));
        var document = new WinoraStateRestoreRecoveryDocument(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            documentStatus,
            [
                new WinoraStateRestoreEntryDocument(
                    "data/app-settings.json",
                    "app-settings.json.0123456789abcdef0123456789abcdef.restore.tmp",
                    "app-settings.json.0123456789abcdef0123456789abcdef.restore.lkg",
                    original,
                    staging,
                    entryStatus),
            ]);
        var serializer = new JsonDocumentSerializer();
        await File.WriteAllBytesAsync(
            paths.WinoraStateRestoreRecoveryFile,
            serializer.Serialize(serializer.CreateEnvelope(
                "winora-state-restore-recovery",
                new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
                document)));

        var service = new WinoraStateBackupService(paths);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InspectPendingRestoreAsync(CancellationToken.None).AsTask());
        Assert.True(File.Exists(paths.WinoraStateRestoreRecoveryFile));
    }

    private static void Write(string root, string relativePath, string value)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static WinoraStateRestoreRecoveryDocument RestoreDocument(
        WinoraStateRestoreRecoveryStatus status,
        params WinoraStateRestoreEntryDocument[] entries) =>
        new(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            status,
            Array.AsReadOnly(entries));

    private static WinoraStateRestoreEntryDocument RestoreEntry(
        string logicalKey,
        char nonceCharacter,
        WinoraStateFileSnapshotDocument? original,
        WinoraStateFileSnapshotDocument staging,
        WinoraStateRestoreEntryStatus status)
    {
        var leaf = logicalKey[(logicalKey.LastIndexOf('/') + 1)..];
        var nonce = new string(nonceCharacter, 32);
        return new WinoraStateRestoreEntryDocument(
            logicalKey,
            $"{leaf}.{nonce}.restore.tmp",
            $"{leaf}.{nonce}.restore.lkg",
            original,
            staging,
            status);
    }

    private static WinoraStateFileSnapshotDocument RestoreSnapshot(
        ulong fileIndex,
        char hashCharacter) =>
        new(1, fileIndex, 1, new string(hashCharacter, 64));

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

    private static Process StartStateRestoreCrashProcess(
        string root,
        string readyPath,
        string mode = "state-restore-crash-after-publication")
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(Winora.Infrastructure.ProcessHost.ProcessHostMarker).Assembly.Location);
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add("unused");
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start the state-restore crash helper.");
    }
}

internal sealed class AttemptLeafReplacementObserver(
    string targetPath,
    string replacementPath) : IValidatedFileObserver
{
    private int _attempted;

    internal Exception? ReplacementFailure { get; private set; }

    public void OnValidated(
        string path,
        ValidatedFileIdentity identity,
        ValidatedFileUse use)
    {
        if (use != ValidatedFileUse.PublicRead ||
            !StringComparer.OrdinalIgnoreCase.Equals(path, targetPath) ||
            Interlocked.Exchange(ref _attempted, 1) != 0)
        {
            return;
        }

        try
        {
            File.Move(targetPath, targetPath + ".displaced");
            File.Move(replacementPath, targetPath);
        }
        catch (Exception exception)
        {
            ReplacementFailure = exception;
        }
    }
}

internal sealed class ReplaceTargetAfterPreflightHook(
    string targetPath,
    string replacementPath,
    string displacedPath) : IWinoraStateRestoreRaceHook
{
    internal bool WasInvoked { get; private set; }

    public void AfterInitialTargetValidation(WinoraStateRestorePublicationContext context)
    {
        if (WasInvoked ||
            !StringComparer.OrdinalIgnoreCase.Equals(context.TargetPath, targetPath))
        {
            return;
        }

        File.Move(targetPath, displacedPath);
        File.Move(replacementPath, targetPath);
        WasInvoked = true;
    }
}

internal sealed class FailSecondApplyAndRollbackOperations : IAtomicFileOperations
{
    private readonly WindowsAtomicFileOperations _inner = new();
    private int _renameCalls;

    public void RenameNoReplace(
        ValidatedFileHandle sourceFile,
        string destinationPath)
    {
        var call = Interlocked.Increment(ref _renameCalls);
        if (call == 4)
        {
            throw new InjectedSecondApplyFailure();
        }

        if (call == 6)
        {
            throw new InjectedCompensatingRollbackFailure();
        }

        _inner.RenameNoReplace(sourceFile, destinationPath);
    }

    public void Delete(ValidatedFileHandle file) => _inner.Delete(file);
}

internal sealed class InjectedSecondApplyFailure : IOException;

internal sealed class InjectedCompensatingRollbackFailure : IOException;

internal sealed class FailFirstMatchingCleanupOperations(string suffix) : IAtomicFileOperations
{
    private readonly WindowsAtomicFileOperations _inner = new();
    private int _failureInjected;

    public void RenameNoReplace(
        ValidatedFileHandle sourceFile,
        string destinationPath) =>
        _inner.RenameNoReplace(sourceFile, destinationPath);

    public void Delete(ValidatedFileHandle file)
    {
        if (file.CurrentPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
            Interlocked.Exchange(ref _failureInjected, 1) == 0)
        {
            throw new InjectedRestoreCleanupFailure();
        }

        _inner.Delete(file);
    }
}

internal sealed class ThrowAfterPublicationHook : IWinoraStateRestoreRaceHook
{
    public void AfterInitialTargetValidation(WinoraStateRestorePublicationContext context)
    {
    }

    public void AfterPublicationBeforeJournal(WinoraStateRestorePublicationContext context) =>
        throw new InjectedAfterPublicationFailure();
}

internal sealed class InjectedRestoreCleanupFailure : IOException;

internal sealed class InjectedAfterPublicationFailure : IOException;
