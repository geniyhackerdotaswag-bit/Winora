namespace Winora.System.Windows;

/// <summary>
/// The icon fonts Winora draws its glyphs with, newest first.
/// </summary>
/// <remarks>
/// <para>
/// "Segoe Fluent Icons" ships with Windows 11. Windows 10 does not have it, and neither do the
/// stripped builds that people who tune Windows tend to run. The family was hard-coded to it, so on
/// such a machine every icon in the navigation pane drew as an empty box — reported from a fresh
/// install on another computer on 2026-09-03.
/// </para>
/// <para>
/// A comma-separated family is the fallback mechanism, not a formatting choice: the text engine
/// takes the first family that has the glyph. WinUI declares its own <c>SymbolThemeFontFamily</c>
/// exactly this way, which is where this string came from — see <c>generic.xaml</c> in the Windows
/// App SDK package.
/// </para>
/// <para>
/// The first attempt at this read the registry to find out which font was installed and chose one.
/// It worked in tests and stopped the app from starting at all, because the answer had to be put
/// into application resources from <c>App</c>'s constructor, where nothing catches a throw and no
/// log exists yet. This holds the same knowledge as a constant instead, and Windows does the
/// choosing — which it was always going to do better.
/// </para>
/// </remarks>
public static class IconFonts
{
    /// <summary>What Windows 11 has, and what the icon catalog was drawn against.</summary>
    public const string PreferredFamily = "Segoe Fluent Icons";

    /// <summary>What Windows 10 has. Same code points for every icon Winora uses.</summary>
    public const string FallbackFamily = "Segoe MDL2 Assets";

    /// <summary>
    /// Both, in order, for a <c>FontFamily</c>.
    /// </summary>
    /// <remarks>
    /// No space after the comma: WinUI's own declaration has none, and a family name is matched
    /// literally, so a stray space is a family nobody has.
    /// </remarks>
    public const string Family = PreferredFamily + "," + FallbackFamily;
}
