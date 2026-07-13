using Winora.Core.Changes;

namespace Winora.Core.Contracts;

public sealed record BackupReceipt(
    string BackupId,
    string BackupDigest,
    string PlanDigest,
    StateFingerprint CapturedSourceFingerprint,
    StateFingerprint LiveSourceFingerprint,
    bool IsVerified)
{
    public static BackupReceipt Verified(
        string backupId,
        string backupDigest,
        string planDigest,
        StateFingerprint capturedSourceFingerprint,
        StateFingerprint liveSourceFingerprint) =>
        new(backupId, backupDigest, planDigest, capturedSourceFingerprint, liveSourceFingerprint, true);
}

public interface IBackupRepository
{
    ValueTask<BackupReceipt> ReadAndVerifyAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken);

    ValueTask<BackupReceipt> CreateAndVerifyAsync(
        ChangePlan plan,
        CancellationToken cancellationToken);

    ValueTask<BackupReceipt> CreateRecoveryCheckpointAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken);
}
