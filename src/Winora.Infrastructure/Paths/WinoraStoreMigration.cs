namespace Winora.Infrastructure.Paths;

/// <summary>What a migration attempt did.</summary>
public enum StoreMigrationOutcome
{
    /// <summary>There was nothing at the old location worth moving.</summary>
    NothingToMove,

    /// <summary>The new location already holds data, so the old one was left untouched.</summary>
    AlreadyMigrated,

    /// <summary>The store now lives at the new location.</summary>
    Moved,

    /// <summary>The move did not happen. Both locations are exactly as they were.</summary>
    Failed,
}

/// <summary>
/// Moves Winora's store out of package-local storage.
/// </summary>
/// <remarks>
/// <para>
/// The store used to sit under <c>LocalApplicationData</c>, which a packaged app has redirected into
/// its own container — and Windows deletes that container when the package is removed. Measured on
/// 2026-08-04: a remove-then-install cycle destroyed the journal, the plan archive and every backup,
/// while the registry changes those backups existed to undo stayed applied. A tool that promises
/// reversibility cannot keep the only means of reversing things somewhere the uninstaller wipes.
/// </para>
/// <para>
/// The new home is under the user profile, which is not redirected — the cursor and sound folders
/// already prove that, since the user drops files there and the packaged app reads them. It needs no
/// elevation, is per-user without any SID handling, and carries the same default protection as the
/// old location.
/// </para>
/// <para>
/// The move is deliberately all-or-nothing. A half-migrated store is worse than either whole one,
/// so a failure leaves both locations exactly as they were and says so.
/// </para>
/// </remarks>
public sealed class WinoraStoreMigration
{
    private readonly string _legacyRoot;
    private readonly string _currentRoot;

    public WinoraStoreMigration(string legacyRoot, string currentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRoot);

        _legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        _currentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentRoot));

        if (string.Equals(_legacyRoot, _currentRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The two store locations must differ.", nameof(currentRoot));
        }
    }

    /// <summary>Creates the migration for this user's real folders.</summary>
    public static WinoraStoreMigration ForCurrentUser() =>
        new(LegacyRootForCurrentUser(), WinoraDataPaths.RootForCurrentUser());

    /// <summary>Where the store used to live, before it was moved out of the package container.</summary>
    public static string LegacyRootForCurrentUser() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "Winora");

    public StoreMigrationOutcome Run()
    {
        try
        {
            if (!HasContent(_legacyRoot))
            {
                return StoreMigrationOutcome.NothingToMove;
            }

            // Never merge. If the new store already holds anything, it is the live one, and pouring
            // an older copy on top of it could resurrect operations that were already retired.
            if (HasContent(_currentRoot))
            {
                return StoreMigrationOutcome.AlreadyMigrated;
            }

            var parent = Path.GetDirectoryName(_currentRoot);
            if (parent is { Length: > 0 })
            {
                Directory.CreateDirectory(parent);
            }

            // An empty directory at the destination would make Move fail; it carries nothing, so
            // removing it loses nothing.
            if (Directory.Exists(_currentRoot))
            {
                Directory.Delete(_currentRoot);
            }

            // Atomic within a volume, and both locations live under the same user profile. A
            // cross-volume layout would throw, which is reported rather than papered over with a
            // copy that could half-finish.
            Directory.Move(_legacyRoot, _currentRoot);
            return StoreMigrationOutcome.Moved;
        }
        catch (Exception)
        {
            return StoreMigrationOutcome.Failed;
        }
    }

    /// <remarks>
    /// An empty directory does not count. The old location is left behind as an empty shell by
    /// earlier versions, and treating that as data would block the migration forever.
    /// </remarks>
    private static bool HasContent(string root)
    {
        try
        {
            return Directory.Exists(root) &&
                Directory.EnumerateFileSystemEntries(root).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
