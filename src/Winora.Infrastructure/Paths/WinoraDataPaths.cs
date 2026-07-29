namespace Winora.Infrastructure.Paths;

public sealed class WinoraDataPaths
{
    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
            "CONIN$",
            "CONOUT$",
            "COM¹",
            "COM²",
            "COM³",
            "LPT¹",
            "LPT²",
            "LPT³",
        ],
        StringComparer.OrdinalIgnoreCase);

    public WinoraDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        DataDirectory = Path.Combine(RootDirectory, "Data");
        AppSettingsFile = Path.Combine(DataDirectory, "app-settings.json");
        ChangeIndexFile = Path.Combine(DataDirectory, "change-index.json");
        RecoveryIndexFile = Path.Combine(DataDirectory, "recovery-index.json");
        MutationLeaseFile = Path.Combine(DataDirectory, "mutation-lease.json");
        BackupsDirectory = Path.Combine(RootDirectory, "Backups");
        OperationsDirectory = Path.Combine(RootDirectory, "Operations");
        JournalDirectory = Path.Combine(RootDirectory, "Journal");
        JournalIndexFile = Path.Combine(JournalDirectory, "index.json");
        JournalEventsDirectory = Path.Combine(JournalDirectory, "Events");
        JournalRetentionDirectory = Path.Combine(JournalDirectory, "Retention");
        AssetsDirectory = Path.Combine(RootDirectory, "Assets");
        PendingDirectory = Path.Combine(RootDirectory, "Pending");
        WinoraStateRestoreRecoveryFile = Path.Combine(
            PendingDirectory,
            "winora-state-restore-recovery.json");
        AppSettingsDocument = new ProjectionJsonDestination(this, AppSettingsFile, "app-settings");
        ChangeIndexDocument = new ProjectionJsonDestination(this, ChangeIndexFile, "change-index");
        RecoveryIndexDocument = new ProjectionJsonDestination(this, RecoveryIndexFile, "recovery-index");
        MutationLeaseDocument = new ProjectionJsonDestination(this, MutationLeaseFile, "mutation-lease");
        JournalIndexDocument = new ProjectionJsonDestination(this, JournalIndexFile, "journal-index");
        WinoraStateRestoreRecoveryDocument = new ProjectionJsonDestination(
            this,
            WinoraStateRestoreRecoveryFile,
            "winora-state-restore-recovery");
    }

    public string RootDirectory { get; }

    public string DataDirectory { get; }

    public string AppSettingsFile { get; }

    public string ChangeIndexFile { get; }

    public string RecoveryIndexFile { get; }

    public string MutationLeaseFile { get; }

    public string BackupsDirectory { get; }

    public string OperationsDirectory { get; }

    public string JournalDirectory { get; }

    public string JournalIndexFile { get; }

    public string JournalEventsDirectory { get; }

    public string JournalRetentionDirectory { get; }

    public string AssetsDirectory { get; }

    public string PendingDirectory { get; }

    public string WinoraStateRestoreRecoveryFile { get; }

    public ProjectionJsonDestination AppSettingsDocument { get; }

    public ProjectionJsonDestination ChangeIndexDocument { get; }

    public ProjectionJsonDestination RecoveryIndexDocument { get; }

    public ProjectionJsonDestination MutationLeaseDocument { get; }

    public ProjectionJsonDestination JournalIndexDocument { get; }

    public ProjectionJsonDestination WinoraStateRestoreRecoveryDocument { get; }

    public static WinoraDataPaths ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return new WinoraDataPaths(Path.Combine(localApplicationData, "Winora"));
    }

    internal string GetBackupDirectory(string backupId) =>
        Path.Combine(BackupsDirectory, ValidatePathSegment(backupId, nameof(backupId)));

    internal string GetOperationDirectory(string operationId) =>
        Path.Combine(OperationsDirectory, ValidatePathSegment(operationId, nameof(operationId)));

    internal string GetOperationManifestFile(string operationId) =>
        Path.Combine(GetOperationDirectory(operationId), "manifest.json");

    internal string GetOperationTransitionFile(
        string operationId,
        long revision,
        string transitionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        return Path.Combine(
            GetOperationDirectory(operationId),
            "Transitions",
            $"{revision}-{ValidatePathSegment(transitionId, nameof(transitionId))}.json");
    }

    internal string GetJournalEventFile(string eventId) =>
        Path.Combine(
            JournalEventsDirectory,
            $"{ValidatePathSegment(eventId, nameof(eventId))}.json");

    internal string GetRetentionTransactionDirectory(string transactionId) =>
        Path.Combine(
            JournalRetentionDirectory,
            ValidatePathSegment(transactionId, nameof(transactionId)));

    internal string GetRetentionIntentFile(string transactionId) =>
        Path.Combine(GetRetentionTransactionDirectory(transactionId), "intent.json");

    internal string GetRetentionStateFile(string transactionId) =>
        Path.Combine(GetRetentionTransactionDirectory(transactionId), "state.json");

    public ProjectionJsonDestination GetOperationManifestDocument(string operationId)
    {
        var canonicalId = ValidatePathSegment(operationId, nameof(operationId));
        return new ProjectionJsonDestination(
            this,
            GetOperationManifestFile(canonicalId),
            canonicalId);
    }

    public AuthoritativeJsonDestination GetOperationTransitionDocument(
        string operationId,
        long revision,
        string transitionId)
    {
        var canonicalTransitionId = ValidatePathSegment(transitionId, nameof(transitionId));
        return new AuthoritativeJsonDestination(
            this,
            GetOperationTransitionFile(operationId, revision, canonicalTransitionId),
            canonicalTransitionId);
    }

    public AuthoritativeJsonDestination GetJournalEventDocument(string eventId)
    {
        var canonicalId = ValidatePathSegment(eventId, nameof(eventId));
        return new AuthoritativeJsonDestination(this, GetJournalEventFile(canonicalId), canonicalId);
    }

    internal AuthoritativeJsonDestination GetRetentionIntentDocument(string transactionId)
    {
        var canonicalId = ValidatePathSegment(transactionId, nameof(transactionId));
        return new AuthoritativeJsonDestination(
            this,
            GetRetentionIntentFile(canonicalId),
            $"retention-intent-{canonicalId}");
    }

    internal ProjectionJsonDestination GetRetentionStateDocument(string transactionId)
    {
        var canonicalId = ValidatePathSegment(transactionId, nameof(transactionId));
        return new ProjectionJsonDestination(
            this,
            GetRetentionStateFile(canonicalId),
            $"retention-state-{canonicalId}");
    }

    public AuthoritativeJsonDestination GetBackupStagingManifestDocument(string backupId)
    {
        var canonicalId = ValidatePathSegment(backupId, nameof(backupId));
        return new AuthoritativeJsonDestination(
            this,
            Path.Combine(BackupsDirectory, $"{canonicalId}.staging", "manifest.json"),
            canonicalId);
    }

    public AuthoritativeJsonDestination GetBackupCommittedManifestDocument(string backupId)
    {
        var canonicalId = ValidatePathSegment(backupId, nameof(backupId));
        return new AuthoritativeJsonDestination(
            this,
            Path.Combine(BackupsDirectory, canonicalId, "manifest.committed.json"),
            canonicalId);
    }

    internal string EnsureOwnedFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(RootDirectory, fullPath);
        if (relativePath == "." ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The path must remain under the fixed Winora data root.", nameof(path));
        }

        return fullPath;
    }

    private static string ValidatePathSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var baseName = value.Split('.', 2)[0];
        if (value.Length > 128 ||
            value != value.Trim() ||
            value.Any(character =>
                !char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character) &&
                character is not '-' and not '_') ||
            value is "." or ".." ||
            value.EndsWith('.') ||
            ReservedDeviceNames.Contains(baseName) ||
            Path.IsPathRooted(value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The identifier must be one safe file-name segment.", parameterName);
        }

        return value;
    }
}
