namespace Winora.System.Updates;

/// <summary>Where this copy of the program is, and where an installed one belongs.</summary>
public interface IAppInstallLocation
{
    /// <summary>The file this process was started from.</summary>
    string CurrentExecutablePath { get; }

    /// <summary>The folder an installed copy lives in.</summary>
    string InstalledDirectory { get; }

    /// <summary>The file an installed copy is.</summary>
    string InstalledExecutablePath { get; }

    /// <summary>True when this process is running from the installed place, under that name.</summary>
    bool IsInstalled { get; }
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// The name is part of the answer, not only the folder. The build produces
/// <c>Winora.App.exe</c> and the release is published as <c>Winora.exe</c>; requiring both to match
/// means a build run out of the debugger is never mistaken for an installed copy, and development
/// never tries to update itself out from under the debugger.
/// </para>
/// <para>
/// <c>%LOCALAPPDATA%\Programs</c> and not <c>Program Files</c>: that folder belongs to the user, so
/// installing and later replacing a file there needs no administrator rights. Winora asks for
/// elevation for the operations that genuinely require it and for nothing else, and putting itself
/// somewhere that made every update an elevation prompt would break that.
/// </para>
/// </remarks>
public sealed class AppInstallLocation : IAppInstallLocation
{
    /// <summary>The folder name, and the file name, an installed copy uses.</summary>
    private const string ProductName = "Winora";

    private const string ExecutableName = "Winora.exe";

    public AppInstallLocation()
        : this(
            Environment.ProcessPath ?? string.Empty,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs"))
    {
    }

    public AppInstallLocation(string currentExecutablePath, string programsRoot)
    {
        ArgumentNullException.ThrowIfNull(currentExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(programsRoot);

        CurrentExecutablePath = currentExecutablePath;
        InstalledDirectory = Path.Combine(programsRoot, ProductName);
        InstalledExecutablePath = Path.Combine(InstalledDirectory, ExecutableName);
    }

    public string CurrentExecutablePath { get; }

    public string InstalledDirectory { get; }

    public string InstalledExecutablePath { get; }

    public bool IsInstalled => Same(CurrentExecutablePath, InstalledExecutablePath);

    /// <summary>
    /// Whether two paths name the same file, as Windows would judge it.
    /// </summary>
    /// <remarks>
    /// Compared after <see cref="Path.GetFullPath(string)" />, which settles forward slashes and
    /// <c>..</c> segments, and ignoring case, which is what the file system does. Comparing the
    /// strings as typed would answer "not installed" for a path that differs only in how somebody
    /// wrote it, and the program would offer to install itself on top of itself.
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
            // A path the file system will not even parse is not the installed one.
            return false;
        }
    }
}
