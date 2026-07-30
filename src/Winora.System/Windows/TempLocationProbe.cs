using System.Runtime.InteropServices;
using System.Text;

namespace Winora.System.Windows;

/// <summary>Whether Winora may ever reclaim from a location.</summary>
public enum TempLocationClassification
{
    /// <summary>Owned by the signed-in user and eligible for reclamation.</summary>
    UserOwned,

    /// <summary>Serviced by Windows. Enumerated so it can be shown as off-limits, never touched.</summary>
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

/// <summary>Enumerates candidate reclamation locations. Never mutates anything.</summary>
public interface ITempLocationProbe
{
    IReadOnlyList<TempLocation> Locations();

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
    public const string ReasonWindowsServiced = "winora.cleanup.windows-serviced";
    public const string ReasonUpdateServicing = "winora.cleanup.update-servicing";
    public const string ReasonPreviousInstallation = "winora.cleanup.previous-installation";

    public IReadOnlyList<TempLocation> Locations()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

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

            // Everything below is serviced by Windows. Reclaiming from these breaks servicing or the
            // ability to roll an update back, so they exist here only to be displayed as refused.
            Protected("windows-temp", Path.Combine(windows, "Temp"), ReasonWindowsServiced),
            Protected("software-distribution", Path.Combine(windows, "SoftwareDistribution"), ReasonUpdateServicing),
            Protected("cbs-logs", Path.Combine(windows, "Logs", "CBS"), ReasonUpdateServicing),
            Protected("windows-old", Path.Combine(Path.GetPathRoot(windows) ?? @"C:\", "Windows.old"), ReasonPreviousInstallation),
        ];
    }

    public TempLocationSurvey Survey(TempLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Classification != TempLocationClassification.UserOwned)
        {
            throw new InvalidOperationException(
                $"'{location.Id}' is not user-owned and is never surveyed for reclamation.");
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

        while (walk.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = walk.Pop();

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
