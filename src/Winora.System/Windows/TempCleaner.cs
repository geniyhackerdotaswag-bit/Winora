namespace Winora.System.Windows;

/// <param name="DeletedCount">Files removed.</param>
/// <param name="DeletedBytes">Space freed.</param>
/// <param name="SkippedCount">Files left in place, almost always because a process holds them open.</param>
public sealed record TempCleanResult(int DeletedCount, long DeletedBytes, int SkippedCount);

/// <summary>Permanently removes files from a user-owned temporary location.</summary>
public interface ITempCleaner
{
    TempCleanResult Clean(TempLocation location, CancellationToken cancellationToken);
}

/// <summary>
/// Deletes outright, bypassing the Recycle Bin, so the space is freed immediately.
/// </summary>
/// <remarks>
/// <para>
/// This is irreversible by design and by the owner's decision: a cleaner whose files sit in the
/// Recycle Bin has not actually freed anything. Only locations the probe classifies as user-owned
/// are accepted; the Windows-serviced ones are refused here as well as being absent from the UI, so
/// a future caller cannot reach them by mistake.
/// </para>
/// <para>
/// A file held open by a running process is skipped and counted rather than fought over. Reporting
/// what was actually removed matters more than emptying the directory: temporary directories are in
/// constant use, and a cleaner that claimed to empty one would be lying.
/// </para>
/// </remarks>
public sealed class WindowsTempCleaner : ITempCleaner
{
    private readonly IElevationProbe _elevation;
    private readonly Func<string?> _runningProgramFolder;

    public WindowsTempCleaner(IElevationProbe elevation)
        : this(elevation, static () => RunningProgramFolder.Path)
    {
    }

    /// <param name="runningProgramFolder">
    /// Where the running program's own files are. Injected so the one rule that matters here — that
    /// Winora never deletes the files it is executing from — can be proved without a test having to
    /// be a single-file build unpacked into a temporary folder.
    /// </param>
    public WindowsTempCleaner(IElevationProbe elevation, Func<string?> runningProgramFolder)
    {
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _runningProgramFolder = runningProgramFolder ?? throw new ArgumentNullException(nameof(runningProgramFolder));
    }

    public TempCleanResult Clean(TempLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);

        // Re-checked here and not merely upstream: this is the call that deletes, and it must not
        // depend on a caller having asked the same question correctly.
        if (!TempReclamationPolicy.CanReclaim(location, _elevation.IsElevated))
        {
            throw new InvalidOperationException(
                $"'{location.Id}' cannot be reclaimed at this privilege level and is never cleaned.");
        }

        var deleted = 0;
        var bytes = 0L;
        var skipped = 0;

        if (!Directory.Exists(location.Path))
        {
            return new TempCleanResult(0, 0, 0);
        }

        var directories = new List<string>();
        var mine = _runningProgramFolder();

        foreach (var file in Enumerate(location.Path, directories, mine, ref skipped))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(file);
                var length = info.Length;

                // Installers and archivers leave read-only scratch behind; clearing the attribute is
                // part of removing our own candidate, not a way to force past a protected file.
                if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    info.Attributes = FileAttributes.Normal;
                }

                File.Delete(file);
                deleted++;
                bytes += length;
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        // Deepest first, and only ever when already empty, so a skipped file keeps its path alive.
        foreach (var directory in directories.OrderByDescending(static path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();

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

        return new TempCleanResult(deleted, bytes, skipped);
    }

    /// <param name="protectedFolder">
    /// The running program's own folder, which is never walked into.
    /// </param>
    /// <remarks>
    /// A single-file Winora is unpacked into the very temporary folder this offers to empty, and
    /// most of those files are not held by Windows: measured on 2026-08-27, 473 of its own 549
    /// unpacked files were free to delete while it was running. Winora would have taken them out
    /// from under itself, and the failure shows up later — at the next screen that needs a lazily
    /// loaded assembly, or at the next start, which finds an incomplete bundle and dies before any
    /// of its own code can say so.
    /// </remarks>
    private static List<string> Enumerate(
        string root,
        List<string> directories,
        string? protectedFolder,
        ref int skipped)
    {
        var files = new List<string>();
        var walk = new Stack<string>();
        walk.Push(root);

        while (walk.Count > 0)
        {
            var directory = walk.Pop();

            // Not counted as skipped: these were never candidates, and reporting them as files
            // something else was holding would be a different and untrue statement.
            if (RunningProgramFolder.Holds(directory, protectedFolder))
            {
                continue;
            }

            try
            {
                // A reparse point can leave the location entirely; following one could delete files
                // somewhere Winora was never pointed at.
                if (!StringComparer.OrdinalIgnoreCase.Equals(directory, root) &&
                    new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped++;
                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (RunningProgramFolder.Holds(child, protectedFolder))
                    {
                        continue;
                    }

                    walk.Push(child);
                    directories.Add(child);
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
