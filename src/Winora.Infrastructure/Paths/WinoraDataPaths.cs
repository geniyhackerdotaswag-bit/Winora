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
        BackupsDirectory = Path.Combine(RootDirectory, "Backups");
        OperationsDirectory = Path.Combine(RootDirectory, "Operations");
        JournalDirectory = Path.Combine(RootDirectory, "Journal");
        JournalIndexFile = Path.Combine(JournalDirectory, "index.json");
        JournalEventsDirectory = Path.Combine(JournalDirectory, "Events");
        AssetsDirectory = Path.Combine(RootDirectory, "Assets");
        PendingDirectory = Path.Combine(RootDirectory, "Pending");
    }

    public string RootDirectory { get; }

    public string DataDirectory { get; }

    public string AppSettingsFile { get; }

    public string ChangeIndexFile { get; }

    public string RecoveryIndexFile { get; }

    public string BackupsDirectory { get; }

    public string OperationsDirectory { get; }

    public string JournalDirectory { get; }

    public string JournalIndexFile { get; }

    public string JournalEventsDirectory { get; }

    public string AssetsDirectory { get; }

    public string PendingDirectory { get; }

    public static WinoraDataPaths ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return new WinoraDataPaths(Path.Combine(localApplicationData, "Winora"));
    }

    public string GetBackupDirectory(string backupId) =>
        Path.Combine(BackupsDirectory, ValidatePathSegment(backupId, nameof(backupId)));

    public string GetOperationDirectory(string operationId) =>
        Path.Combine(OperationsDirectory, ValidatePathSegment(operationId, nameof(operationId)));

    public string GetOperationManifestFile(string operationId) =>
        Path.Combine(GetOperationDirectory(operationId), "manifest.json");

    public string GetOperationTransitionFile(
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

    public string GetJournalEventFile(string eventId) =>
        Path.Combine(
            JournalEventsDirectory,
            $"{ValidatePathSegment(eventId, nameof(eventId))}.json");

    public string EnsureOwnedFilePath(string path)
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
