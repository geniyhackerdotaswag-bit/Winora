using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Backups;

public sealed class WinoraStateBackupService
{
    private readonly BackupRepository _repository;
    private readonly WinoraStateSnapshotCapture _capture;
    private readonly WinoraStateRestorer _restorer;

    public WinoraStateBackupService(WinoraDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _repository = new BackupRepository(paths, new UnsupportedSystemCaptureProvider());
        _capture = new WinoraStateSnapshotCapture(paths);
        _restorer = new WinoraStateRestorer(paths);
    }

    public ValueTask<WinoraStateBackupReceipt> CreateAsync(
        string backupId,
        CancellationToken cancellationToken)
    {
        var capture = _capture.Capture(cancellationToken);
        return _repository.CreateWinoraStateAsync(
            backupId,
            capture,
            cancellationToken);
    }

    public async ValueTask<WinoraStateBackupReceipt> VerifyAsync(
        string backupId,
        CancellationToken cancellationToken)
    {
        var backup = await _repository.ReadWinoraStateAsync(
            backupId,
            cancellationToken).ConfigureAwait(false);
        return new WinoraStateBackupReceipt(
            backupId,
            backup.Manifest.BackupDigest,
            true);
    }

    public async ValueTask RestoreAsync(
        string backupId,
        CancellationToken cancellationToken)
    {
        var backup = await _repository.ReadWinoraStateAsync(
            backupId,
            cancellationToken).ConfigureAwait(false);
        _restorer.Restore(backup.Artifacts, cancellationToken);
    }

    public ValueTask<WinoraStateRestoreRecoveryInfo?> InspectPendingRestoreAsync(
        CancellationToken cancellationToken) =>
        _restorer.InspectPendingRecoveryAsync(cancellationToken);

    public ValueTask RecoverPendingRestoreAsync(CancellationToken cancellationToken) =>
        _restorer.RecoverPendingAsync(cancellationToken);

    private sealed class UnsupportedSystemCaptureProvider : Winora.Core.Contracts.IBackupCaptureProvider
    {
        public ValueTask<Winora.Core.Contracts.BackupCapture> CaptureOperationAsync(
            Winora.Core.Changes.ChangePlan plan,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Winora.Core.Contracts.BackupCapture> CaptureRecoveryCheckpointAsync(
            Winora.Core.Changes.RollbackPlan plan,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
