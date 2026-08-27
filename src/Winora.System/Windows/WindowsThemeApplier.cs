namespace Winora.System.Windows;

/// <summary>How applying a theme ended.</summary>
public enum WindowsThemeApplyOutcome
{
    Applied,

    /// <summary>The theme Windows says is current is not on disk.</summary>
    CurrentThemeMissing,

    /// <summary>The edited copy could not be written.</summary>
    CouldNotWrite,

    /// <summary>
    /// The Windows Settings window is already open, and it swallows the request.
    /// </summary>
    /// <remarks>
    /// Reported rather than worked around. Closing a window the person opened themselves, to push
    /// through a change they can make in that very window, is not Winora's to do.
    /// </remarks>
    SettingsWindowOpen,

    /// <summary>Windows was handed the file and the change never showed up.</summary>
    NotConfirmed,
}

/// <summary>What Windows currently holds, and where its theme file is.</summary>
public interface IWindowsThemeState
{
    /// <summary>
    /// The live mode and accent, read from where Windows keeps them.
    /// </summary>
    /// <remarks>
    /// The registry, not the theme file. Measured on 2026-08-27: the file said
    /// <c>SystemMode=Light</c> while the machine had been dark for hours. The file records what was
    /// last applied through it, which is not the same thing as what is on the screen.
    /// </remarks>
    WindowsThemeSettings Read();

    /// <summary>The file Windows says is the current theme, or null.</summary>
    string? CurrentThemePath();
}

/// <summary>Hands a theme file to Windows.</summary>
public interface IThemeLauncher
{
    /// <summary>
    /// Opens the file the way a double click would.
    /// </summary>
    /// <remarks>
    /// The registered action for <c>.theme</c> is
    /// <c>rundll32 themecpl.dll,OpenThemeAction</c>, which hands the file to the Settings app. An
    /// earlier note in this project recorded that command as doing nothing while the file
    /// association worked; they are the same mechanism, and the difference was
    /// <see cref="IsSettingsOpen" />.
    /// </remarks>
    void Start(string themePath);

    /// <summary>
    /// Whether a Settings window is already open.
    /// </summary>
    /// <remarks>
    /// Measured on 2026-08-27: with one already running, handing over a theme did nothing at all —
    /// no error, no change, no trace. With none running the theme applied in under half a second.
    /// </remarks>
    bool IsSettingsOpen();
}

/// <summary>
/// Changes the Windows theme by writing a copy of the current one and handing it back.
/// </summary>
/// <remarks>
/// <para>
/// A copy of the theme already in use, with a few lines changed, rather than a theme of Winora's
/// own: everything the file says about wallpaper, cursors and sounds carries over untouched, so
/// changing the colours cannot change the desktop picture.
/// </para>
/// <para>
/// Applying a theme opens the Windows Settings window. There is no quiet route — this was looked
/// for and does not exist — and the screen says so before the button is pressed rather than after.
/// </para>
/// <para>
/// Nearly every rule in here comes from one experiment on 2026-08-27 that applied a deliberately
/// wrong colour and put it back. The version written before that experiment compiled, passed its
/// tests, and would have failed on the machine it was written on.
/// </para>
/// </remarks>
public sealed class WindowsThemeApplier
{
    /// <summary>
    /// The two names the edited copy alternates between.
    /// </summary>
    /// <remarks>
    /// Windows ignores a theme file that is already the current one. With a single fixed name the
    /// first press would work and every press after it would do nothing, silently — the failure
    /// most likely to reach a user, because the developer only ever presses the button once.
    /// </remarks>
    private static readonly string[] FileNames = ["Winora.theme", "Winora 2.theme"];

    private readonly IWindowsThemeState _state;
    private readonly IThemeLauncher _launcher;
    private readonly string _themesFolder;
    private readonly int _attempts;
    private readonly TimeSpan _pause;

    public WindowsThemeApplier(
        IWindowsThemeState state,
        IThemeLauncher launcher,
        string themesFolder,
        int attempts = 30,
        TimeSpan? pause = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        ArgumentException.ThrowIfNullOrWhiteSpace(themesFolder);
        _themesFolder = themesFolder;
        _attempts = attempts;
        _pause = pause ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// Applies a mode and accent, and does not return success until Windows agrees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The waiting is the point. Windows adopts a theme through its own Settings process, so the
    /// call that hands over the file returns before anything has changed. Failing to confirm is
    /// reported as a failure, never as a success nobody checked.
    /// </para>
    /// <para>
    /// Undo goes through here too, with the mode and accent that were recorded before the change.
    /// It rebuilds from whatever theme is current at that moment rather than from a file saved
    /// earlier, so anything the person changed in between — wallpaper, cursors, sounds — survives
    /// being put back. Windows rewrites and deletes applied theme files anyway, so a saved path
    /// would name a file it has since edited or removed.
    /// </para>
    /// <para>
    /// One theme per call, and there is no second pass. Handing Windows a colour and then handing
    /// back the "choose it yourself" setting was tried and does not work: applying a theme leaves
    /// the Settings window open, and a theme handed over while it is open is ignored. So the
    /// setting stays off once this feature is used, which the screen says before the button is
    /// pressed rather than after.
    /// </para>
    /// </remarks>
    public async Task<WindowsThemeApplyOutcome> ApplyAsync(
        WindowsThemeSettings wanted,
        CancellationToken cancellationToken = default)
    {
        var source = _state.CurrentThemePath();

        if (source is null || !File.Exists(source))
        {
            return WindowsThemeApplyOutcome.CurrentThemeMissing;
        }

        byte[] edited;

        try
        {
            edited = WindowsThemeFile.With(File.ReadAllBytes(source), wanted);
        }
        catch (Exception)
        {
            return WindowsThemeApplyOutcome.CouldNotWrite;
        }

        return await HandOverAsync(edited, wanted, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WindowsThemeApplyOutcome> HandOverAsync(
        byte[] theme,
        WindowsThemeSettings expected,
        CancellationToken cancellationToken)
    {
        if (_launcher.IsSettingsOpen())
        {
            return WindowsThemeApplyOutcome.SettingsWindowOpen;
        }

        string destination;

        try
        {
            Directory.CreateDirectory(_themesFolder);
            destination = Destination(_state.CurrentThemePath());
            File.WriteAllBytes(destination, theme);
        }
        catch (Exception)
        {
            return WindowsThemeApplyOutcome.CouldNotWrite;
        }

        _launcher.Start(destination);

        return await ConfirmAsync(expected, cancellationToken).ConfigureAwait(false)
            ? WindowsThemeApplyOutcome.Applied
            : WindowsThemeApplyOutcome.NotConfirmed;
    }

    /// <summary>The first of the two names that is not the theme already in use.</summary>
    private string Destination(string? current)
    {
        foreach (var name in FileNames)
        {
            var candidate = Path.Combine(_themesFolder, name);

            if (current is null ||
                !string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return Path.Combine(_themesFolder, FileNames[0]);
    }

    /// <summary>
    /// Waits for Windows to hold what was asked for, and gives up rather than assuming.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three facts are checked: the mode, whether Windows is choosing the accent, and the colour
    /// itself when one is known. The colour is checked even on the pass that hands the choice back
    /// to Windows — that pass follows one that set the colour explicitly, and the whole point is
    /// that the colour stays put.
    /// </para>
    /// <para>
    /// Against the single value the theme sets, never against the shades Windows derives from it.
    /// Those move on their own and would fail a change that worked.
    /// </para>
    /// </remarks>
    private async Task<bool> ConfirmAsync(WindowsThemeSettings expected, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var live = _state.Read();

            var arrived = live.Mode == expected.Mode &&
                live.IsAccentAutomatic == expected.IsAccentAutomatic &&
                (expected.Accent is not { } accent || live.Accent == accent);

            if (arrived)
            {
                return true;
            }

            if (_pause > TimeSpan.Zero)
            {
                await Task.Delay(_pause, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }
}
