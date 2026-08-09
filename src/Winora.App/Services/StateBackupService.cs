using Winora.Infrastructure.Backups;

namespace Winora.App.Services;

/// <param name="BackupId">Identity, and what a restore is asked for by.</param>
/// <param name="CreatedAtUtc">When the snapshot was taken.</param>
/// <param name="IsVerified">True when its recorded digest still matches its contents.</param>
public sealed record StateBackupView(string BackupId, DateTimeOffset CreatedAtUtc, bool IsVerified);

/// <summary>Snapshots of Winora's own records, for the presentation layer.</summary>
public interface IStateBackupService
{
    Task<IReadOnlyList<StateBackupView>> ReadAsync(CancellationToken cancellationToken = default);

    Task<bool> CreateAsync(CancellationToken cancellationToken = default);

    Task<bool> RestoreAsync(string backupId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class StateBackupService : IStateBackupService
{
    private readonly IStateBackupCatalog _catalog;

    public StateBackupService(IStateBackupCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<IReadOnlyList<StateBackupView>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await _catalog.ReadAsync(cancellationToken).ConfigureAwait(false);

        return entries
            .Select(static entry => new StateBackupView(
                entry.BackupId,
                entry.CreatedAtUtc,
                entry.IsVerified))
            .ToArray();
    }

    public async Task<bool> CreateAsync(CancellationToken cancellationToken = default) =>
        await _catalog.CreateAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <remarks>
    /// Resolved against the catalog rather than trusted, so a caller cannot ask for a restore from
    /// an arbitrary folder name — the same rule the cursor and icon services follow.
    /// </remarks>
    public async Task<bool> RestoreAsync(
        string backupId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _catalog.ReadAsync(cancellationToken).ConfigureAwait(false);
        var known = entries.Any(entry =>
            string.Equals(entry.BackupId, backupId, StringComparison.OrdinalIgnoreCase));

        return known && await _catalog.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
    }
}
