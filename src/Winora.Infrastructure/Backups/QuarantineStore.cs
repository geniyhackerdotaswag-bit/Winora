using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Backups;

/// <param name="RelativePath">Path relative to the surveyed location, preserved on both sides.</param>
/// <param name="Length">Size in bytes at the time of the move.</param>
/// <param name="LastWriteUtc">Last write time at the time of the move.</param>
public sealed record QuarantineItem(string RelativePath, long Length, DateTimeOffset LastWriteUtc);

/// <param name="Items">Everything that moved.</param>
/// <param name="SkippedCount">Entries that could not be moved, typically because they were in use.</param>
public sealed record QuarantineResult(IReadOnlyList<QuarantineItem> Items, int SkippedCount)
{
    public long TotalBytes => Items.Sum(static item => item.Length);
}

/// <summary>
/// Moves files into and out of a Winora-owned quarantine directory.
/// </summary>
/// <remarks>
/// <para>
/// Quarantine exists so that reclamation is reversible. Nothing here deletes anything: freeing the
/// space is a separate retention decision, taken later and confirmed on its own.
/// </para>
/// <para>
/// Identity is recorded as relative path, length, and last-write time rather than a content hash.
/// A same-volume move is a directory-entry rename, so the bytes are the same bytes by construction;
/// hashing several gigabytes of temporary files on every operation would cost minutes to guard
/// against filesystem corruption the move itself cannot introduce. A cross-volume location is
/// refused rather than copied, which is what keeps that reasoning true.
/// </para>
/// </remarks>
public sealed class QuarantineStore
{
    private readonly WinoraQuarantinePaths _paths;

    public QuarantineStore(WinoraQuarantinePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string DirectoryFor(string operationId) => _paths.DirectoryFor(operationId);

    /// <summary>True when a move from <paramref name="sourceDirectory"/> would be a rename.</summary>
    public bool IsSameVolume(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var source = Path.GetPathRoot(Path.GetFullPath(sourceDirectory));
        var destination = Path.GetPathRoot(Path.GetFullPath(_paths.QuarantineDirectory));
        return source is not null &&
            destination is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(source, destination);
    }

    public QuarantineResult Move(string operationId, string sourceDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        if (!IsSameVolume(sourceDirectory))
        {
            throw new InvalidOperationException(
                "Quarantine only accepts a source on the same volume, so the move stays a rename.");
        }

        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var moved = new List<QuarantineItem>();
        var skipped = 0;

        foreach (var file in SafeEnumerate(source, ref skipped))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(file);
                var relative = Path.GetRelativePath(source, file);
                var destination = _paths.ResolveOwnedPath(operationId, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var length = info.Length;
                var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

                File.Move(file, destination, overwrite: false);
                moved.Add(new QuarantineItem(relative, length, lastWrite));
            }
            catch (Exception)
            {
                // A file held open by a running process stays where it is and is counted, so the
                // caller can report what was actually reclaimed instead of implying everything was.
                skipped++;
            }
        }

        return new QuarantineResult(moved, skipped);
    }

    /// <summary>Puts everything back where it came from. Idempotent: an item already home is left alone.</summary>
    public QuarantineResult Restore(string operationId, string sourceDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        var root = _paths.DirectoryFor(operationId);
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var restored = new List<QuarantineItem>();
        var skipped = 0;

        if (!Directory.Exists(root))
        {
            return new QuarantineResult(restored, 0);
        }

        foreach (var file in SafeEnumerate(root, ref skipped))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(file);
                var relative = Path.GetRelativePath(root, file);
                var destination = Path.GetFullPath(Path.Combine(target, relative));

                var length = info.Length;
                var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(file, destination, overwrite: false);
                restored.Add(new QuarantineItem(relative, length, lastWrite));
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        // Leaving an empty directory skeleton behind would make "is this operation still holding
        // anything" ambiguous, and leaves litter in a folder the user can see.
        PruneEmptyDirectories(root);

        return new QuarantineResult(restored, skipped);
    }

    /// <summary>Whether this operation still holds anything.</summary>
    public bool IsEmpty(string operationId)
    {
        var root = _paths.DirectoryFor(operationId);
        return !Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any();
    }

    /// <summary>
    /// Removes directories that hold nothing, deepest first, including the operation root. Only ever
    /// deletes empty directories, so a skipped file keeps its whole path alive.
    /// </summary>
    private static void PruneEmptyDirectories(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static path => path.Length))
            {
                TryDeleteIfEmpty(directory);
            }

            TryDeleteIfEmpty(root);
        }
        catch (Exception)
        {
            // Litter is not worth failing a completed restore over.
        }
    }

    private static void TryDeleteIfEmpty(string directory)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception)
        {
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, ref int skipped)
    {
        var files = new List<string>();
        var walk = new Stack<string>();
        walk.Push(root);

        while (walk.Count > 0)
        {
            var directory = walk.Pop();

            try
            {
                // A reparse point can leave the surveyed location and land somewhere Winora must not
                // touch, so it is never followed.
                if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped++;
                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    walk.Push(child);
                }

                files.AddRange(Directory.EnumerateFiles(directory));
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        return files;
    }
}
