namespace Winora.Infrastructure.Backups;

public sealed class BackupFingerprintMismatchException : InvalidOperationException
{
    public BackupFingerprintMismatchException()
        : base("The captured, live, and confirmed source fingerprints do not match.")
    {
    }
}

public sealed record WinoraStateBackupReceipt(
    string BackupId,
    string BackupDigest,
    bool IsVerified);
