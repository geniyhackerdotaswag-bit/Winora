using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Backups;

public enum WinoraStateRestoreRecoveryStatus
{
    Prepared = 0,
    Applying = 1,
    RollingBack = 2,
    RecoveryRequired = 3,
    Completed = 4,
    RolledBack = 5,
    CleanupAfterApply = 6,
    CleanupAfterRollback = 7,
}

public sealed record WinoraStateRestoreRecoveryInfo(
    Guid RecoveryId,
    WinoraStateRestoreRecoveryStatus Status,
    int CompletedStepCount,
    int TotalStepCount);

internal enum WinoraStateRestoreEntryStatus
{
    Prepared = 0,
    Applying = 1,
    Applied = 2,
    RollingBack = 3,
    RolledBack = 4,
}

internal sealed record WinoraStateFileSnapshotDocument(
    uint VolumeSerialNumber,
    ulong FileIndex,
    long Length,
    string Sha256)
{
    internal ValidatedFileIdentity Identity => new(VolumeSerialNumber, FileIndex);

    internal void Validate()
    {
        if (FileIndex == 0 || Length < 0 || !IsUpperHexSha256(Sha256))
        {
            throw new InvalidDataException(
                "A persisted Winora-state restore file snapshot is invalid.");
        }
    }

    private static bool IsUpperHexSha256(string? value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'A' and <= 'F'));
}

internal sealed record WinoraStateRestoreEntryDocument(
    string LogicalKey,
    string TemporaryFileName,
    string LastKnownGoodFileName,
    WinoraStateFileSnapshotDocument? OriginalTarget,
    WinoraStateFileSnapshotDocument Staging,
    WinoraStateRestoreEntryStatus Status)
{
    internal void Validate()
    {
        var normalized = BackupArtifactPath.Normalize(LogicalKey);
        var targetLeaf = LogicalKey[(LogicalKey.LastIndexOf('/') + 1)..];
        if (!StringComparer.Ordinal.Equals(normalized, LogicalKey) ||
            (!LogicalKey.StartsWith("data/", StringComparison.Ordinal) &&
             !LogicalKey.StartsWith("assets/", StringComparison.Ordinal)) ||
            !IsBoundRecoveryLeaf(
                TemporaryFileName,
                targetLeaf,
                ".restore.tmp") ||
            !IsBoundRecoveryLeaf(
                LastKnownGoodFileName,
                targetLeaf,
                ".restore.lkg") ||
            !Enum.IsDefined(Status))
        {
            throw new InvalidDataException(
                "A persisted Winora-state restore entry is invalid.");
        }

        OriginalTarget?.Validate();
        (Staging ?? throw new InvalidDataException(
            "A persisted Winora-state restore entry has no staging snapshot.")).Validate();
        if (OriginalTarget is not null &&
            OriginalTarget.Identity == Staging.Identity)
        {
            throw new InvalidDataException(
                "The original target and staging file must have distinct identities.");
        }
    }

    private static bool IsBoundRecoveryLeaf(
        string? value,
        string targetLeaf,
        string suffix)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 240 ||
            !StringComparer.Ordinal.Equals(Path.GetFileName(value), value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var prefix = $"{targetLeaf}.";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var nonce = value.AsSpan(
            prefix.Length,
            value.Length - prefix.Length - suffix.Length);
        if (nonce.Length != 32)
        {
            return false;
        }

        foreach (var character in nonce)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record WinoraStateRestoreRecoveryDocument(
    Guid RecoveryId,
    DateTimeOffset CreatedUtc,
    WinoraStateRestoreRecoveryStatus Status,
    IReadOnlyList<WinoraStateRestoreEntryDocument> Entries)
{
    internal void Validate()
    {
        if (RecoveryId == Guid.Empty ||
            CreatedUtc == default ||
            CreatedUtc.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(Status) ||
            Entries is null ||
            Entries.Count == 0)
        {
            throw new InvalidDataException(
                "The Winora-state restore recovery record is invalid.");
        }

        var logicalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var temporaryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lkgNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            (entry ?? throw new InvalidDataException(
                "The Winora-state restore recovery record contains a null entry.")).Validate();
            if (!logicalKeys.Add(entry.LogicalKey) ||
                !temporaryNames.Add(entry.TemporaryFileName) ||
                !lkgNames.Add(entry.LastKnownGoodFileName))
            {
                throw new InvalidDataException(
                    "The Winora-state restore recovery record contains duplicate identities.");
            }
        }

        ValidateStatusProgression();
    }

    private void ValidateStatusProgression()
    {
        var statuses = Entries.Select(entry => entry.Status).ToArray();
        var valid = Status switch
        {
            WinoraStateRestoreRecoveryStatus.Prepared =>
                IsValidPreparedProgress(statuses),
            WinoraStateRestoreRecoveryStatus.Applying =>
                IsValidApplyingProgress(statuses),
            WinoraStateRestoreRecoveryStatus.RollingBack =>
                IsValidRollingBackProgress(statuses),
            WinoraStateRestoreRecoveryStatus.RecoveryRequired =>
                statuses.Any(status => status != WinoraStateRestoreEntryStatus.RolledBack) &&
                (IsValidPreRollbackProgress(statuses) ||
                 IsValidRollingBackProgress(statuses)),
            WinoraStateRestoreRecoveryStatus.CleanupAfterApply or
                WinoraStateRestoreRecoveryStatus.Completed =>
                statuses.All(status => status == WinoraStateRestoreEntryStatus.Applied),
            WinoraStateRestoreRecoveryStatus.CleanupAfterRollback or
                WinoraStateRestoreRecoveryStatus.RolledBack =>
                statuses.All(status => status == WinoraStateRestoreEntryStatus.RolledBack),
            _ => false,
        };

        if (!valid)
        {
            throw new InvalidDataException(
                "The Winora-state restore document status contradicts its persisted entry progress.");
        }
    }

    private static bool IsValidApplyingProgress(
        IReadOnlyList<WinoraStateRestoreEntryStatus> statuses)
    {
        return IsValidPreRollbackProgress(statuses) &&
            statuses.Any(status => status is
                WinoraStateRestoreEntryStatus.Applying or
                WinoraStateRestoreEntryStatus.Applied);
    }

    private static bool IsValidPreparedProgress(
        IReadOnlyList<WinoraStateRestoreEntryStatus> statuses) =>
        statuses.All(status => status == WinoraStateRestoreEntryStatus.Prepared);

    private static bool IsValidPreRollbackProgress(
        IReadOnlyList<WinoraStateRestoreEntryStatus> statuses)
    {
        var index = 0;
        while (index < statuses.Count &&
               statuses[index] == WinoraStateRestoreEntryStatus.Applied)
        {
            index++;
        }

        var hasApplying = index < statuses.Count &&
            statuses[index] == WinoraStateRestoreEntryStatus.Applying;
        if (hasApplying)
        {
            index++;
        }

        while (index < statuses.Count &&
               statuses[index] == WinoraStateRestoreEntryStatus.Prepared)
        {
            index++;
        }

        return index == statuses.Count;
    }

    private static bool IsValidRollingBackProgress(
        IReadOnlyList<WinoraStateRestoreEntryStatus> statuses)
    {
        var rollbackIndex = 0;
        while (rollbackIndex < statuses.Count &&
               statuses[rollbackIndex] is not
                   WinoraStateRestoreEntryStatus.RollingBack and not
                   WinoraStateRestoreEntryStatus.RolledBack)
        {
            rollbackIndex++;
        }

        if (rollbackIndex == statuses.Count ||
            !IsValidPreRollbackProgress(statuses.Take(rollbackIndex).ToArray()))
        {
            return false;
        }

        return statuses.Skip(rollbackIndex).All(status => status is
            WinoraStateRestoreEntryStatus.RollingBack or
            WinoraStateRestoreEntryStatus.RolledBack);
    }

    internal bool IsTerminal =>
        Status is WinoraStateRestoreRecoveryStatus.Completed or
            WinoraStateRestoreRecoveryStatus.RolledBack;

    internal WinoraStateRestoreRecoveryInfo ToInfo() =>
        new(
            RecoveryId,
            Status,
            Entries.Count(entry =>
                entry.Status is WinoraStateRestoreEntryStatus.Applied or
                    WinoraStateRestoreEntryStatus.RolledBack),
            Entries.Count);

    internal WinoraStateRestoreRecoveryDocument WithEntryStatus(
        int index,
        WinoraStateRestoreEntryStatus entryStatus,
        WinoraStateRestoreRecoveryStatus documentStatus)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var entries = Entries.ToArray();
        entries[index] = entries[index] with { Status = entryStatus };
        return this with
        {
            Status = documentStatus,
            Entries = Array.AsReadOnly(entries),
        };
    }
}

internal sealed class WinoraStateRestoreRecoveryStore
{
    private readonly WinoraDataPaths _paths;
    private readonly AtomicJsonFile _documents;

    internal WinoraStateRestoreRecoveryStore(
        WinoraDataPaths paths,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _documents = new AtomicJsonFile(
            paths,
            (JsonDocumentSerializer?)null,
            timeProvider);
    }

    internal async ValueTask<WinoraStateRestoreRecoveryDocument?> ReadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var read = await _documents.ReadProjectionAsync<WinoraStateRestoreRecoveryDocument>(
                _paths.WinoraStateRestoreRecoveryDocument,
                cancellationToken).ConfigureAwait(false);
            read.Document.Payload.Validate();
            return read.Document.Payload;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    internal async ValueTask WriteAsync(
        WinoraStateRestoreRecoveryDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        await _documents.WriteProjectionAsync(
            _paths.WinoraStateRestoreRecoveryDocument,
            document,
            cancellationToken).ConfigureAwait(false);
    }
}
