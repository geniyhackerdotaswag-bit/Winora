namespace Winora.System.Updates;

/// <summary>Where this copy of the program is.</summary>
public interface IAppInstallLocation
{
    /// <summary>The file this process was started from.</summary>
    string CurrentExecutablePath { get; }

    /// <summary>The folder that file sits in. Everything Winora keeps lives here.</summary>
    string InstalledDirectory { get; }

    /// <summary>The file an update replaces.</summary>
    string InstalledExecutablePath { get; }

    /// <summary>True when this copy can replace itself where it stands.</summary>
    bool IsInstalled { get; }
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Winora is portable: it lives where it was put, updates itself there, and keeps its files
/// beside itself. There is no installed place and no other place — the folder holding
/// <c>Winora.exe</c> is the answer to all three questions above.
/// </para>
/// <para>
/// It used to copy itself into <c>%LOCALAPPDATA%\Programs\Winora</c> and treat only that copy as
/// real: a copy running from anywhere else was refused self-update and sent to the download page
/// instead. The owner had that removed on 2026-09-03 — a program that greets a new user by asking
/// to move itself somewhere they did not choose is asking the wrong question, and being told your
/// copy cannot update itself is worse than the problem it avoided.
/// </para>
/// <para>
/// The name is still part of the answer. The build produces <c>Winora.App.exe</c> and the release
/// is published as <c>Winora.exe</c>; requiring the release name means a build run out of the
/// debugger never tries to update itself out from under the debugger.
/// </para>
/// </remarks>
public sealed class AppInstallLocation : IAppInstallLocation
{
    /// <summary>The file name a released copy has.</summary>
    private const string ExecutableName = "Winora.exe";

    public AppInstallLocation()
        : this(Environment.ProcessPath ?? string.Empty)
    {
    }

    public AppInstallLocation(string currentExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(currentExecutablePath);

        CurrentExecutablePath = currentExecutablePath;

        // Environment.ProcessPath, never AppContext.BaseDirectory: this is a single-file build, and
        // BaseDirectory points at the unpacked copy under %TEMP% rather than at the file the user
        // double-clicked. Writing beside that would put the store somewhere Windows empties.
        InstalledDirectory = DirectoryOf(currentExecutablePath);

        InstalledExecutablePath = InstalledDirectory.Length == 0
            ? string.Empty
            : Path.Combine(InstalledDirectory, ExecutableName);
    }

    public string CurrentExecutablePath { get; }

    public string InstalledDirectory { get; }

    public string InstalledExecutablePath { get; }

    public bool IsInstalled => Same(CurrentExecutablePath, InstalledExecutablePath);

    /// <summary>
    /// The folder a path sits in, or empty when the path cannot be read as one.
    /// </summary>
    /// <remarks>
    /// Empty rather than a throw. This is reached from the very first lines of startup, before
    /// there is a window to show a failure in or a log to write it to, and a process path Windows
    /// will not parse must not be the reason the program never opens.
    /// </remarks>
    private static string DirectoryOf(string executablePath)
    {
        if (executablePath.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Whether two paths name the same file, as Windows would judge it.
    /// </summary>
    /// <remarks>
    /// Compared after <see cref="Path.GetFullPath(string)" />, which settles forward slashes and
    /// <c>..</c> segments, and ignoring case, which is what the file system does. Comparing the
    /// strings as typed would answer "no" for a path that differs only in how somebody wrote it.
    /// </remarks>
    private static bool Same(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
