namespace Winora.System.Windows;

/// <summary>
/// The folder the running program's own files are in, so cleaning never takes them.
/// </summary>
/// <remarks>
/// <para>
/// A single-file Winora is unpacked into <c>%TEMP%\.net\Winora\&lt;id&gt;</c> before it starts, which
/// puts several hundred of its own files inside the very folder its cleanup screen offers to empty.
/// </para>
/// <para>
/// Measured on 2026-08-27 while the program was running: of 549 unpacked files, Windows held only
/// 76. The other 473 — <c>DWriteCore.dll</c>, <c>MainWindow.xaml</c>, most of the assemblies that
/// load only when a screen first needs them — would have been deleted by Winora's own cleanup, out
/// from under Winora. The program keeps working until it reaches for one of them, and the next
/// start finds an incomplete bundle and dies before any of its code runs.
/// </para>
/// <para>
/// So the running copy's own folder is not a candidate. Unpacked copies of <em>other</em> versions
/// still are, and clearing those is the whole point — see <c>ExtractionCache</c>, which does it at
/// startup without being asked.
/// </para>
/// </remarks>
public static class RunningProgramFolder
{
    /// <summary>
    /// Where the running program's files are, or null when that cannot be established.
    /// </summary>
    /// <remarks>
    /// For a single-file build this is the unpacked copy; for an ordinary build it is the output
    /// folder, which is nowhere near a temporary directory and so excludes nothing.
    /// </remarks>
    public static string? Path
    {
        get
        {
            try
            {
                var folder = AppContext.BaseDirectory;

                return string.IsNullOrWhiteSpace(folder)
                    ? null
                    : global::System.IO.Path.TrimEndingDirectorySeparator(
                        global::System.IO.Path.GetFullPath(folder));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Whether a path is the running program's folder or something inside it.
    /// </summary>
    /// <remarks>
    /// A folder that cannot be established protects nothing, which is the safe answer the other way
    /// round from most of this file: refusing to clean everything because one question went
    /// unanswered would make the cleanup screen useless, and the ordinary case — a build that is not
    /// unpacked into the temporary directory at all — has nothing to protect.
    /// </remarks>
    public static bool Holds(string candidate, string? folder)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            // Both sides are normalised, not just the candidate. AppContext.BaseDirectory always
            // ends with a separator, so a caller passing it straight through would have matched
            // nothing at all and protected nothing — which a test caught and a reader would not.
            var full = Normalised(candidate);
            var mine = Normalised(folder);

            if (string.Equals(full, mine, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return full.StartsWith(
                mine + global::System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Normalised(string path) =>
        global::System.IO.Path.TrimEndingDirectorySeparator(
            global::System.IO.Path.GetFullPath(path));
}
