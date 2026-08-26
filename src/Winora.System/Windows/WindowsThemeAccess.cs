using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Winora.System.Windows;

/// <summary>
/// Reads the live appearance from where Windows keeps it.
/// </summary>
/// <remarks>
/// The registry rather than the current theme file. Measured on 2026-08-27: the file on the owner's
/// machine said <c>SystemMode=Light</c> while the desktop had been dark for hours. A theme file
/// records what was last applied through it; it is not a report of the running system.
/// </remarks>
public sealed class WindowsThemeState : IWindowsThemeState
{
    private const string ThemesKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes";
    private const string PersonalizeKeyPath = ThemesKeyPath + @"\Personalize";
    private const string DwmKeyPath = @"Software\Microsoft\Windows\DWM";
    private const string DesktopKeyPath = @"Control Panel\Desktop";

    /// <summary>Where a theme has to sit for Windows to adopt it.</summary>
    public static string UserThemesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft",
        "Windows",
        "Themes");

    /// <summary>
    /// The live mode and accent.
    /// </summary>
    /// <remarks>
    /// The mode is reported only when the system and application settings agree. Windows allows
    /// them to differ, and answering with one of two disagreeing values would make a later
    /// comparison pass or fail on which one this happened to pick.
    /// </remarks>
    public WindowsThemeSettings Read()
    {
        WindowsThemeMode? mode = null;

        if (ReadNumber(PersonalizeKeyPath, "SystemUsesLightTheme") is { } system &&
            ReadNumber(PersonalizeKeyPath, "AppsUseLightTheme") is { } apps &&
            system == apps)
        {
            mode = system == 0 ? WindowsThemeMode.Dark : WindowsThemeMode.Light;
        }

        // DWM\AccentColor, not Explorer's AccentColorMenu: the second is a shade Windows derives,
        // and on this machine it did not equal the accent even before anything was changed.
        var accent = ReadNumber(DwmKeyPath, "AccentColor") is { } stored
            ? WindowsThemeFile.AccentFromExplorer((uint)stored)
            : (int?)null;

        // HKCU\Control Panel\Desktop, which is where the live setting lives — the theme file's
        // AutoColorization line is only what was last applied through a file.
        var automatic = ReadNumber(DesktopKeyPath, "AutoColorization") == 1;

        return new WindowsThemeSettings(mode, accent, automatic);
    }

    /// <summary>The file Windows says is the current theme, or null.</summary>
    public string? CurrentThemePath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ThemesKeyPath, writable: false);

            return key?.GetValue("CurrentTheme") as string is { Length: > 0 } path ? path : null;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether Winora can put a theme file where Windows will read it.
    /// </summary>
    /// <remarks>
    /// Asked by writing, because a folder that exists is not a folder that can be written to, and
    /// the difference only shows up at the moment the change would otherwise fail.
    /// </remarks>
    public static bool IsThemesFolderWritable()
    {
        try
        {
            Directory.CreateDirectory(UserThemesFolder);
            var probe = Path.Combine(UserThemesFolder, ".winora-write-probe");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int? ReadNumber(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);

            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// Hands a theme file to Windows the way a double click does.
/// </summary>
/// <remarks>
/// The registered action for <c>.theme</c> is <c>rundll32 themecpl.dll,OpenThemeAction</c>, which
/// passes the file to the Settings app. Winora goes through the association rather than invoking
/// that command itself: the association is what Windows documents, and it is the same mechanism.
/// </remarks>
public sealed class ShellThemeLauncher : IThemeLauncher
{
    /// <summary>The Settings app, which is what actually adopts a theme.</summary>
    private const string SettingsProcessName = "SystemSettings";

    public void Start(string themePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(themePath);

        using var process = Process.Start(new ProcessStartInfo(themePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(themePath) ?? string.Empty,
        });
    }

    /// <summary>
    /// Whether a Settings window is already open.
    /// </summary>
    /// <remarks>
    /// Measured on 2026-08-27: with one already running, handing over a theme did nothing at all —
    /// no error, no change, no trace in the registry. With none running the theme applied in under
    /// half a second. Winora reports this and stops; closing a window somebody else opened, to push
    /// through a change they could make in that window, is not its call.
    /// </remarks>
    public bool IsSettingsOpen()
    {
        try
        {
            var running = Process.GetProcessesByName(SettingsProcessName);

            foreach (var process in running)
            {
                process.Dispose();
            }

            return running.Length > 0;
        }
        catch (Exception)
        {
            // Unable to look is not the same as none open. Saying "none" here would send the change
            // into the one condition under which it silently does nothing.
            return true;
        }
    }
}
