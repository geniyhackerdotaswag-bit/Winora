using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Backups;

/// <param name="BackupId">Identity, and what a restore is asked for by.</param>
/// <param name="CreatedAtUtc">When the snapshot was taken.</param>
/// <param name="IsVerified">True when its recorded digest still matches its contents.</param>
public sealed record StateBackupEntry(string BackupId, DateTimeOffset CreatedAtUtc, bool IsVerified);

/// <summary>Snapshots of Winora's own records.</summary>
public interface IStateBackupCatalog
{
    Task<IReadOnlyList<StateBackupEntry>> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes a snapshot and returns its identifier.</summary>
    Task<string?> CreateAsync(CancellationToken cancellationToken = default);

    Task<bool> RestoreAsync(string backupId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lists, creates and restores snapshots of Winora's own state.
/// </summary>
/// <remarks>
/// <para>
/// These protect the *ability to undo*, not the settings themselves. Winora's journal, plan archive
/// and per-change backups are what make a change reversible; if that bookkeeping is lost or damaged,
/// changes already made to Windows become permanent whether or not the user wanted them. A snapshot
/// is insurance against that, and the screen has to say so — "backups" otherwise reads as "copies of
/// my Windows settings", which these are not.
/// </para>
/// <para>
/// Entries are found by trying to read each backup folder as a Winora-state snapshot and keeping the
/// ones that verify. Per-change backups live in the same directory, and telling them apart by
/// inspecting the manifest by hand would duplicate validation the repository already does properly.
/// Verification is the point rather than a cost: a snapshot that no longer matches its digest is
/// exactly what the user needs to be told about.
/// </para>
/// </remarks>
public sealed class StateBackupCatalog : IStateBackupCatalog
{
    private readonly WinoraStateBackupService _backups;
    private readonly WinoraDataPaths _paths;

    public StateBackupCatalog(WinoraStateBackupService backups, WinoraDataPaths paths)
    {
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<IReadOnlyList<StateBackupEntry>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.Exists(_paths.BackupsDirectory)
                ? Directory.EnumerateDirectories(_paths.BackupsDirectory)
                : [];
        }
        catch (Exception)
        {
            return [];
        }

        var entries = new List<StateBackupEntry>();

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var backupId = Path.GetFileName(directory);
            if (backupId.Length == 0)
            {
                continue;
            }

            bool verified;
            try
            {
                var receipt = await _backups
                    .VerifyAsync(backupId, cancellationToken)
                    .ConfigureAwait(false);
                verified = receipt.IsVerified;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Either a per-change backup, which belongs to the history screen, or a snapshot
                // that no longer verifies. Both are excluded: offering a restore from something that
                // failed verification would be the opposite of what this screen is for.
                continue;
            }

            entries.Add(new StateBackupEntry(backupId, CreatedAtOf(directory), verified));
        }

        // Newest first: the snapshot a user reaches for is almost always the most recent.
        return entries.OrderByDescending(static entry => entry.CreatedAtUtc).ToArray();
    }

    public async Task<string?> CreateAsync(CancellationToken cancellationToken = default)
    {
        var backupId = Guid.NewGuid().ToString("D");
        try
        {
            await _backups.CreateAsync(backupId, cancellationToken).ConfigureAwait(false);
            return backupId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> RestoreAsync(string backupId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupId))
        {
            return false;
        }

        try
        {
            await _backups.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <remarks>
    /// From the directory, because the manifest records no timestamp. Said plainly rather than
    /// presented as if the backup carried the time itself.
    /// </remarks>
    private static DateTimeOffset CreatedAtOf(string directory)
    {
        try
        {
            return new DateTimeOffset(Directory.GetCreationTimeUtc(directory), TimeSpan.Zero);
        }
        catch (Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
