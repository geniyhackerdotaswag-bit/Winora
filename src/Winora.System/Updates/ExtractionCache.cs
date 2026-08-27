namespace Winora.System.Updates;

/// <summary>
/// Clears away the unpacked copies of Winora versions that have been replaced.
/// </summary>
/// <remarks>
/// <para>
/// A single-file build is unpacked into <c>%TEMP%\.net\&lt;name&gt;\&lt;id&gt;</c> before it starts,
/// and the folder is keyed by build, not by name. Every update therefore leaves a whole extracted
/// copy of the version it replaced — around 206 MB each — and .NET never removes them. On the
/// owner's machine that pile had reached 1.36 GB across eight versions, all of them dead, on a
/// drive that then had nothing left for the ninth.
/// </para>
/// <para>
/// So the program that made the mess clears it. Only its own, only the ones it is not using, and
/// only what will delete without a fight: another copy of Winora may be running from one of these
/// folders, and the operating system holding a file is the answer to whether that is so.
/// </para>
/// </remarks>
public static class ExtractionCache
{
    /// <summary>Where .NET unpacks single-file builds.</summary>
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), ".net");

    /// <summary>
    /// Removes unpacked copies other than the one in use, and reports how many went.
    /// </summary>
    /// <param name="applicationName">The build's own name, which is the folder .NET keys on.</param>
    /// <param name="inUse">
    /// The folder the running program was unpacked into, which is never removed. For a single-file
    /// build this is <see cref="AppContext.BaseDirectory"/>; null when it cannot be determined, and
    /// then nothing is removed at all — deleting the folder underneath a running program is worse
    /// than leaving every copy in place.
    /// </param>
    /// <param name="root">
    /// Where .NET unpacks builds, or null for the real one.
    /// </param>
    /// <remarks>
    /// The root is a parameter so a test can point this at a folder of its own. The first version
    /// read the temporary directory directly, and the test moved <c>TEMP</c> to work around it —
    /// which redirected the temporary directory for every other test running beside it and broke
    /// three of them.
    /// </remarks>
    public static int RemoveReplaced(string applicationName, string? inUse, string? root = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        if (string.IsNullOrWhiteSpace(inUse))
        {
            return 0;
        }

        var removed = 0;

        try
        {
            var folder = Path.Combine(root ?? DefaultRoot, applicationName);

            if (!Directory.Exists(folder))
            {
                return 0;
            }

            var running = Path.GetFullPath(inUse).TrimEnd(Path.DirectorySeparatorChar);

            foreach (var candidate in Directory.EnumerateDirectories(folder))
            {
                var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);

                if (string.Equals(full, running, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryDelete(full))
                {
                    removed++;
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping. A tidy-up that cannot proceed is not a reason to stop a program from
            // starting, and the only cost of leaving these is the disk space they already occupy.
        }

        return removed;
    }

    /// <summary>
    /// Whether the whole folder went.
    /// </summary>
    /// <remarks>
    /// A locked file means another copy of Winora is running from here. That is the whole test:
    /// there is no list to consult, and the operating system's refusal is a better answer than any
    /// guess about which versions might still be in use.
    /// </remarks>
    private static bool TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
