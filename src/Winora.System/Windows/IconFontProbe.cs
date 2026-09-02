using Microsoft.Win32;

namespace Winora.System.Windows;

/// <summary>Which icon font this copy of Windows actually has. Never changes anything.</summary>
public interface IIconFontProbe
{
    /// <summary>The font family the shell should draw its icons with.</summary>
    string ResolveFamily();
}

/// <summary>
/// Picks the icon font that exists on this machine instead of assuming one.
/// </summary>
/// <remarks>
/// <para>
/// "Segoe Fluent Icons" ships with Windows 11. Windows 10 does not have it, and neither do the
/// stripped builds that people who tune Windows tend to run. The family was hard-coded, so on such
/// a machine every icon in the navigation pane drew as an empty box — reported from a fresh install
/// on another computer on 2026-09-03.
/// </para>
/// <para>
/// The fallback is "Segoe MDL2 Assets", which Windows 10 and 11 both ship. It is not a guess that
/// it covers the catalog: Segoe Fluent Icons kept the code points of its predecessor for these
/// icons, and every one of the seventeen was checked against the <c>cmap</c> table of both font
/// files before this class was written. <c>IconFontCoverageTests</c> re-checks it on every run, so
/// an icon added later at a code point only the newer font has cannot ship unnoticed.
/// </para>
/// <para>
/// Asked of the registry rather than of a font enumeration API, because the answer is needed before
/// the first frame is drawn and the registry key is documented, cheap and readable without COM.
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/gdi/font-installation-and-deletion
/// </para>
/// </remarks>
public sealed class IconFontProbe : IIconFontProbe
{
    /// <summary>What Windows 11 has, and what the catalog was drawn against.</summary>
    public const string PreferredFamily = "Segoe Fluent Icons";

    /// <summary>What Windows 10 has. Same code points for every icon Winora uses.</summary>
    public const string FallbackFamily = "Segoe MDL2 Assets";

    private const string FontsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    private readonly Func<string, bool> _isInstalled;

    public IconFontProbe()
        : this(IsInstalled)
    {
    }

    /// <param name="isInstalled">Answers whether a family is installed. Replaced by tests.</param>
    internal IconFontProbe(Func<string, bool> isInstalled) =>
        _isInstalled = isInstalled ?? throw new ArgumentNullException(nameof(isInstalled));

    /// <summary>
    /// The preferred family when it is there, the fallback otherwise.
    /// </summary>
    /// <remarks>
    /// Returns the fallback name even when neither font is installed. There is nothing better to
    /// return: an empty family makes WinUI draw the glyph in the text font, where these code points
    /// are unassigned, which is the same empty box by a longer route.
    /// </remarks>
    public string ResolveFamily() => _isInstalled(PreferredFamily) ? PreferredFamily : FallbackFamily;

    /// <summary>
    /// Whether a font family is registered on this machine, for this user or for everyone.
    /// </summary>
    /// <remarks>
    /// Both hives are read: a font installed for one user only lives under <c>HKEY_CURRENT_USER</c>
    /// and is invisible in the machine hive, and Winora runs elevated — where "current user" is
    /// still the same person, but reading only the machine hive would miss their font.
    /// </remarks>
    private static bool IsInstalled(string family)
    {
        return RegisteredIn(Registry.LocalMachine, family) || RegisteredIn(Registry.CurrentUser, family);
    }

    private static bool RegisteredIn(RegistryKey hive, string family)
    {
        try
        {
            using var fonts = hive.OpenSubKey(FontsKeyPath, writable: false);

            if (fonts is null)
            {
                return false;
            }

            foreach (var name in fonts.GetValueNames())
            {
                // Values read "Segoe Fluent Icons (TrueType)": the family, then the technology in
                // brackets. Matching the whole name would never hit; matching a prefix would let
                // "Segoe UI" answer for "Segoe UI Variable", so the boundary is checked.
                if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase) &&
                    (name.Length == family.Length || name[family.Length] == ' '))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // A machine that will not let its font list be read is not a machine to crash on. The
            // fallback family is the safe answer, and it is what a false gives.
            return false;
        }

        return false;
    }
}
