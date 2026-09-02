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

    /// <summary>
    /// Brings a store from either former home into the current one.
    /// </summary>
    /// <remarks>
    /// Two homes now, tried newest first: the user profile, where the store lived until the program
    /// became fully portable on 2026-09-03, and before that the package container. The profile is
    /// tried first because it is the one that holds a live store — someone who has been running
    /// Winora recently has their journal and backups there, and those are what a missed migration
    /// costs.
    ///
    /// Nothing is done when the current home already holds a store, or when it *is* one of the old
    /// ones — which happens when the program's own folder cannot be written and the profile is used
    /// as the fallback.
    /// </remarks>
    public static StoreMigrationOutcome RunForCurrentUser()
    {
        var current = WinoraDataPaths.RootForCurrentUser();
        var outcome = StoreMigrationOutcome.NothingToMove;

        foreach (var legacy in new[] { WinoraDataPaths.ProfileRoot(), LegacyRootForCurrentUser() })
        {
            if (SameFolder(legacy, current))
            {
                continue;
            }

            outcome = new WinoraStoreMigration(legacy, current).Run();

            if (outcome is StoreMigrationOutcome.Moved or StoreMigrationOutcome.AlreadyMigrated)
            {
                return outcome;
            }
        }

        return outcome;
    }

    /// <summary>Where the store used to live, before it was moved out of the package container.</summary>
    public static string LegacyRootForCurrentUser() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "Winora");

    private static bool SameFolder(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

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

            try
            {
                // Atomic when both are on one volume, which is the ordinary case.
                Directory.Move(_legacyRoot, _currentRoot);
                return StoreMigrationOutcome.Moved;
            }
            catch (IOException)
            {
                // Different volumes. Now that the store follows the program, this is the ordinary
                // case for anyone who keeps Winora off their system drive — the old store is on C:
                // and the new one is wherever they put the folder. Refusing here would quietly cost
                // them their journal and every backup, which is the one thing this program must not
                // do, so the tree is copied instead.
                return CopyAcrossVolumes();
            }
        }
        catch (Exception)
        {
            return StoreMigrationOutcome.Failed;
        }
    }

    /// <summary>
    /// Copies the whole tree, then removes the old one — for when the two are on different volumes.
    /// </summary>
    /// <remarks>
    /// Still all-or-nothing, just in three steps instead of one. Every file is copied, then the
    /// count and the total size are compared against the source, and only a match lets the old
    /// store be deleted. A copy that stopped halfway leaves the old store untouched and takes the
    /// half-written new one away with it, so the next run finds exactly what this one found.
    ///
    /// Sizes rather than hashes: this runs before the first window and a store holding backups can
    /// be hundreds of megabytes. A file that copied to the right length but the wrong content is
    /// not a failure mode <c>File.Copy</c> has.
    /// </remarks>
    private StoreMigrationOutcome CopyAcrossVolumes()
    {
        try
        {
            foreach (var source in Directory.EnumerateFiles(_legacyRoot, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(_currentRoot, Path.GetRelativePath(_legacyRoot, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }

            if (!SameContents(_legacyRoot, _currentRoot))
            {
                Discard(_currentRoot);
                return StoreMigrationOutcome.Failed;
            }

            Directory.Delete(_legacyRoot, recursive: true);
            return StoreMigrationOutcome.Moved;
        }
        catch (Exception)
        {
            Discard(_currentRoot);
            return StoreMigrationOutcome.Failed;
        }
    }

    private static bool SameContents(string left, string right)
    {
        static (int Count, long Bytes) Measure(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(static file => new FileInfo(file))
                .Aggregate((Count: 0, Bytes: 0L), static (total, file) =>
                    (total.Count + 1, total.Bytes + file.Length));

        return Measure(left) == Measure(right);
    }

    /// <remarks>
    /// Removing a half-copied destination is not optional. Left in place it would look like a live
    /// store to the next run, which would then refuse the migration and leave the real one stranded
    /// at the old location for good.
    /// </remarks>
    private static void Discard(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception)
        {
            // Nothing better to do here, and the outcome is already a failure.
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
