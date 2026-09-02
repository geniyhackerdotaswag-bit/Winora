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

    public static WinoraDataPaths ForCurrentUser() => new(RootForCurrentUser());

    /// <summary>The folder the store sits in, beside the program: <c>WinoraData</c>.</summary>
    public const string StoreFolderName = "WinoraData";

    /// <summary>
    /// Where the store lives: in <c>WinoraData</c>, beside <c>Winora.exe</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Winora is portable, and the owner asked on 2026-09-03 for everything it needs to sit in the
    /// folder they put the program in. Move that folder to a memory stick and the journal, the plan
    /// archive, the backups and the profile go with it; delete it and nothing of Winora's is left
    /// behind anywhere.
    /// </para>
    /// <para>
    /// <c>Environment.ProcessPath</c>, never <c>AppContext.BaseDirectory</c>: this is a single-file
    /// build, and <c>BaseDirectory</c> points at the unpacked copy under <c>%TEMP%</c>, which
    /// Windows empties. The store would vanish between runs and every backup with it.
    /// </para>
    /// <para>
    /// Falls back to the user profile when the program's own folder cannot be written — a copy run
    /// straight from a read-only share or a disc. Losing the ability to undo a change is a worse
    /// outcome than keeping the store somewhere the user did not pick, and
    /// <c>WinoraStoreMigration</c> brings an older store across on first run either way.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Resolved once. Six different places ask for this — the diagnostic sink, the profile store,
    /// the bypass store, the settings screen, the migration and the paths themselves — and the
    /// answer costs a directory creation and a test write. Asking each time would repeat that on
    /// every call, and worse: a folder that stopped accepting writes midway through a session would
    /// leave half the program reading one store and half writing another.
    /// </remarks>
    private static readonly Lazy<string> ResolvedRoot = new(
        static () =>
        {
            var beside = BesideTheProgram();

            return beside.Length > 0 && CanWriteInto(beside) ? beside : ProfileRoot();
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string RootForCurrentUser() => ResolvedRoot.Value;

    /// <summary>Where the store would go if the program's folder allows it. Empty when unknown.</summary>
    public static string BesideTheProgram()
    {
        try
        {
            var executable = Environment.ProcessPath;

            if (string.IsNullOrEmpty(executable))
            {
                return string.Empty;
            }

            var folder = Path.GetDirectoryName(Path.GetFullPath(executable));

            return string.IsNullOrEmpty(folder) ? string.Empty : Path.Combine(folder, StoreFolderName);
        }
        catch (Exception)
        {
            // Reached before there is a window or a log; an unparseable process path must not be
            // the reason the program never opens.
            return string.Empty;
        }
    }

    /// <summary>The old home, and the fallback: <c>%USERPROFILE%\Winora\State</c>.</summary>
    public static string ProfileRoot() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify),
            "Winora",
            "State");

    /// <summary>
    /// Whether a folder can actually be created and written in, tested by doing it.
    /// </summary>
    /// <remarks>
    /// Tested rather than inferred from access-control rules: the rules are not the only thing that
    /// refuses a write. A read-only volume, a full disk and a folder an administrator has locked all
    /// answer "allowed" to the rules and "no" to the file system.
    /// </remarks>
    private static bool CanWriteInto(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-probe");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
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
