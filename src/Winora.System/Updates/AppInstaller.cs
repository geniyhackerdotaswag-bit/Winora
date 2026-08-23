using System.Diagnostics;

namespace Winora.System.Updates;

/// <summary>How an installation attempt ended.</summary>
public enum InstallOutcome
{
    /// <summary>The program is now in the place it belongs.</summary>
    Installed,

    /// <summary>It was already there and nothing needed doing.</summary>
    AlreadyInstalled,

    /// <summary>The copy could not be made. Nothing has changed.</summary>
    CopyFailed,
}

/// <summary>Puts a downloaded copy of Winora into the place an installed one belongs.</summary>
public interface IAppInstaller
{
    /// <summary>True when this copy is running from somewhere other than the installed place.</summary>
    bool NeedsInstalling { get; }

    /// <summary>Where the copy would go. Shown to the person before they agree.</summary>
    string DestinationPath { get; }

    InstallOutcome Install();

    /// <summary>Starts the installed copy. The caller ends this process afterwards.</summary>
    bool StartInstalledCopy();
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Asked, never assumed. A program that copied itself somewhere on first launch without saying so
/// has done something the person did not ask for, and the exact destination goes on the screen
/// before they answer.
/// </para>
/// <para>
/// The downloaded file is left where it is. It belongs to whoever downloaded it, tidying it away was
/// not part of the bargain, and a program that deleted files out of somebody's Downloads folder
/// would be doing something worse than leaving one behind.
/// </para>
/// </remarks>
public sealed class AppInstaller : IAppInstaller
{
    /// <summary>What the shortcut is called in the Start menu.</summary>
    private const string ShortcutName = "Winora.lnk";

    private const string ShortcutDescription = "Winora";

    private readonly IAppInstallLocation _location;
    private readonly IShortcutWriter _shortcuts;
    private readonly string _startMenuDirectory;

    public AppInstaller(IAppInstallLocation location, IShortcutWriter shortcuts)
        : this(
            location,
            shortcuts,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs"))
    {
    }

    public AppInstaller(
        IAppInstallLocation location,
        IShortcutWriter shortcuts,
        string startMenuDirectory)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _shortcuts = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
        ArgumentException.ThrowIfNullOrWhiteSpace(startMenuDirectory);
        _startMenuDirectory = startMenuDirectory;
    }

    public bool NeedsInstalling => !_location.IsInstalled;

    public string DestinationPath => _location.InstalledExecutablePath;

    public InstallOutcome Install()
    {
        if (_location.IsInstalled)
        {
            return InstallOutcome.AlreadyInstalled;
        }

        try
        {
            Directory.CreateDirectory(_location.InstalledDirectory);
            File.Copy(_location.CurrentExecutablePath, DestinationPath, overwrite: true);
        }
        catch (Exception)
        {
            // Out of disk, or a folder policy forbids it. Nothing has moved; the program keeps
            // running from where it is and says so.
            return InstallOutcome.CopyFailed;
        }

        // Deliberately not part of the outcome. A program in the right place without a menu entry is
        // still a working program, and refusing the installation over a shortcut would trade
        // something for nothing.
        _shortcuts.Write(
            Path.Combine(_startMenuDirectory, ShortcutName),
            DestinationPath,
            ShortcutDescription);

        return InstallOutcome.Installed;
    }

    public bool StartInstalledCopy()
    {
        try
        {
            var started = Process.Start(new ProcessStartInfo(DestinationPath)
            {
                WorkingDirectory = _location.InstalledDirectory,
                UseShellExecute = false,
            });

            return started is not null;
        }
        catch (Exception)
        {
            // The copy is in place and opens from the Start menu; only this hand-off failed.
            return false;
        }
    }
}
