using System.Runtime.InteropServices;
using System.Text;

namespace Winora.System.Windows;

/// <summary>Whether Winora may ever reclaim from a location.</summary>
public enum TempLocationClassification
{
    /// <summary>Owned by the signed-in user and eligible for reclamation.</summary>
    UserOwned,

    /// <summary>
    /// Serviced by Windows but reclaimable by an administrator. Whether Winora may touch it depends
    /// on how this process was started, which is a fact about the process, not about the location.
    /// </summary>
    RequiresElevation,

    /// <summary>
    /// Never reclaimed by Winora at any privilege level. Windows.old is the case: there is no
    /// documented programmatic removal, and deleting it closes the route back to the previous
    /// Windows version for good.
    /// </summary>
    Protected,

    /// <summary>The path could not be resolved on this machine.</summary>
    Unavailable,
}

/// <param name="Id">Stable display-safe identifier.</param>
/// <param name="Path">Absolute path, or empty when unavailable.</param>
/// <param name="Classification">Whether reclamation may ever consider this location.</param>
/// <param name="ReasonCode">Localizable reason a protected location is off-limits; null otherwise.</param>
public sealed record TempLocation(
    string Id,
    string Path,
    TempLocationClassification Classification,
    string? ReasonCode);

/// <param name="FileCount">Files counted, excluding those that could not be read.</param>
/// <param name="TotalBytes">Total size of the counted files.</param>
/// <param name="UnreadableEntryCount">Entries skipped because they were locked or denied.</param>
/// <param name="IsFullyEnumerated">False when anything was skipped.</param>
public sealed record TempLocationSurvey(
    TempLocation Location,
    int FileCount,
    long TotalBytes,
    int UnreadableEntryCount,
    bool IsFullyEnumerated);

/// <summary>
/// The single rule for what Winora may reclaim from.
/// </summary>
/// <remarks>
/// One place, because the probe, the cleaner and the screen each used to decide for themselves and
/// a disagreement between them is either a refusal the user cannot explain or a deletion nobody
/// authorised.
/// </remarks>
public static class TempReclamationPolicy
{
    public static bool CanReclaim(TempLocation location, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Classification switch
        {
            TempLocationClassification.UserOwned => true,
            TempLocationClassification.RequiresElevation => isElevated,
            _ => false,
        };
    }
}

/// <summary>Enumerates candidate reclamation locations. Never mutates anything.</summary>
public interface ITempLocationProbe
{
    IReadOnlyList<TempLocation> Locations();

    /// <summary>True when this process holds the rights the Windows-serviced locations need.</summary>
    bool IsElevated { get; }

    TempLocationSurvey Survey(TempLocation location, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the documented temporary locations and states plainly which are off-limits. The
/// protected entries are listed rather than omitted so the screen can show why Winora will not
/// touch them, instead of leaving a later contributor to rediscover the rule.
/// </summary>
/// <remarks>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-gettemppath2w
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath
/// </remarks>
public sealed class WindowsTempLocationProbe : ITempLocationProbe
{
    public const string ReasonUpdateServicing = "winora.cleanup.update-servicing";

    /// <summary>
    /// Servicing logs are only a record. Clearing them costs the diagnostic trail and nothing else,
    /// so they must not borrow the update cache's warning about losing rollback.
    /// </summary>
    public const string ReasonServicingLogs = "winora.cleanup.servicing-logs";

    public const string ReasonPreviousInstallation = "winora.cleanup.previous-installation";

    private readonly IElevationProbe _elevation;

    public WindowsTempLocationProbe(IElevationProbe elevation)
    {
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
    }

    public bool IsElevated => _elevation.IsElevated;

    /// <summary>
    /// Every id this probe can produce, whether or not the location exists on this machine.
    /// </summary>
    /// <remarks>
    /// The action-journal allowlist is built from this. Deriving it from the same candidate list the
    /// probe uses is what stops the two drifting: a location added below becomes journalable in the
    /// same edit, rather than deleting bytes that no entry ever records.
    /// </remarks>
    public static IReadOnlyList<string> AllLocationIds { get; } =
        Candidates(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .Select(static location => location.Id)
            .ToArray();

    public IReadOnlyList<TempLocation> Locations()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        return Candidates(windows)
            // A location that is not on this disk is not a decision the user has to make. Windows.old
            // in particular exists only for a few days after a feature update, and listing it the
            // rest of the time showed a row about reclaiming a folder that was not there.
            .Where(static location =>
                location.Classification == TempLocationClassification.UserOwned ||
                Directory.Exists(location.Path))
            .ToArray();
    }

    private static IEnumerable<TempLocation> Candidates(string windows)
    {
        return
        [
            UserOwned("user-temp", UserTempPath()),

            // FOLDERID_InternetCache is deliberately absent. Inside an AppContainer it resolves to
            // the package's own cache under Packages\<pfn>\AC\INetCache, so listing it would offer
            // Winora's private cache as if it were the user's browsing cache. Measured on the
            // packaged build, not assumed.
            UserOwned("crash-dumps", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrashDumps")),

            // Serviced by Windows, but an administrator may clear them. Whether Winora offers to is
            // decided by the running process's token, not fixed here.
            // No reason code: clearing the Windows temp folder costs nothing worth stating, and an
            // empty resource string rendered as the raw key on screen.
            NeedsElevation("windows-temp", Path.Combine(windows, "Temp"), reasonCode: null),
            NeedsElevation("software-distribution", Path.Combine(windows, "SoftwareDistribution"), ReasonUpdateServicing),
            NeedsElevation("cbs-logs", Path.Combine(windows, "Logs", "CBS"), ReasonServicingLogs),

            // Refused at every privilege level: no documented programmatic removal, and deleting it
            // permanently closes the route back to the previous Windows version.
            Protected("windows-old", Path.Combine(Path.GetPathRoot(windows) ?? @"C:\", "Windows.old"), ReasonPreviousInstallation),
        ];
    }

    public TempLocationSurvey Survey(TempLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!TempReclamationPolicy.CanReclaim(location, _elevation.IsElevated))
        {
            throw new InvalidOperationException(
                $"'{location.Id}' cannot be reclaimed at this privilege level and is never surveyed.");
        }

        var files = 0;
        var bytes = 0L;
        var unreadable = 0;

        if (!Directory.Exists(location.Path))
        {
            return new TempLocationSurvey(location, 0, 0, 0, true);
        }

        var walk = new Stack<string>();
        walk.Push(location.Path);

        // What the cleaner will not take must not be counted as what it will free. The running
        // program's own unpacked files sit inside this very folder, and a figure that included them
        // would promise back two hundred megabytes that nothing is ever going to delete.
        var mine = RunningProgramFolder.Path;

        while (walk.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = walk.Pop();

            if (RunningProgramFolder.Holds(directory, mine))
            {
                continue;
            }

            // A reparse point can leave the location and land somewhere Winora must not touch.
            try
            {
                if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    unreadable++;
                    continue;
                }
            }
            catch (Exception)
            {
                unreadable++;
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    walk.Push(child);
                }
            }
            catch (Exception)
            {
                unreadable++;
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                        files++;
                    }
                    catch (Exception)
                    {
                        unreadable++;
                    }
                }
            }
            catch (Exception)
            {
                unreadable++;
            }
        }

        return new TempLocationSurvey(location, files, bytes, unreadable, unreadable == 0);
    }

    private static TempLocation UserOwned(string id, string path) =>
        string.IsNullOrWhiteSpace(path)
            ? new TempLocation(id, string.Empty, TempLocationClassification.Unavailable, null)
            : new TempLocation(id, Path.TrimEndingDirectorySeparator(path), TempLocationClassification.UserOwned, null);

    private static TempLocation NeedsElevation(string id, string path, string? reasonCode) =>
        new(
            id,
            Path.TrimEndingDirectorySeparator(path),
            TempLocationClassification.RequiresElevation,
            reasonCode);

    private static TempLocation Protected(string id, string path, string reasonCode) =>
        new(id, Path.TrimEndingDirectorySeparator(path), TempLocationClassification.Protected, reasonCode);

    /// <summary>
    /// Prefers the documented <c>GetTempPath2W</c>, which returns the system temp path for processes
    /// running as SYSTEM and the per-user path otherwise. Falls back to the older API on builds that
    /// do not export it.
    /// </summary>
    private static string UserTempPath()
    {
        try
        {
            var buffer = new StringBuilder(600);
            var length = GetTempPath2W((uint)buffer.Capacity, buffer);
            if (length > 0 && length < buffer.Capacity)
            {
                return buffer.ToString(0, (int)length);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Older build: the documented predecessor is still correct for a per-user process.
        }
        catch (DllNotFoundException)
        {
        }

        return Path.GetTempPath();
    }

    // Classic DllImport: the LibraryImport generator cannot marshal a StringBuilder, and the
    // alternatives it accepts would require disabling runtime marshalling for the whole assembly.
    [DllImport("kernel32.dll", EntryPoint = "GetTempPath2W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetTempPath2W(uint bufferLength, StringBuilder buffer);
}
