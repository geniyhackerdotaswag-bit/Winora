using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Winora.System.Windows;

namespace Winora.App.Controls;

/// <summary>
/// Turns one icon catalog key into something WinUI can draw.
/// </summary>
/// <remarks>
/// Separate from <see cref="FluentIconCatalog"/> on purpose: the catalog is linked into
/// <c>Winora.App.Tests</c> by source and has to stay free of WinUI types, because a plain xUnit
/// host cannot activate them. The catalog answers what an icon *is*; this answers what to put on
/// screen for it, and only the second half needs WinUI.
/// </remarks>
public static class CatalogIcon
{
    /// <summary>The size every icon in the shell is drawn at.</summary>
    public const double Size = 20;

    /// <summary>
    /// The icon font, built once.
    /// </summary>
    /// <remarks>
    /// Once rather than per icon: the pane builds seventeen of these while the window opens.
    ///
    /// It used to name "Segoe Fluent Icons" alone. That font is Windows 11 only, so on Windows 10
    /// and on stripped builds every icon in the pane drew as an empty box. <see cref="IconFonts"/>
    /// names both, and the text engine picks the one that has the glyph.
    /// </remarks>
    private static readonly Lazy<FontFamily> IconFont = new(
        static () => new FontFamily(IconFonts.Family),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Resolves one catalog key to an icon, of whichever kind the catalog holds it as.
    /// </summary>
    /// <remarks>
    /// Returns null for a key the catalog does not know, and that is not a silent shrug: a route
    /// naming an unknown key is caught by <c>IconCatalogTests</c> before it can ship. It shipped
    /// once — the bypass route asked for "shield", which was never in the catalog, so the item sat
    /// in the pane with a blank space where every neighbour had an icon and nothing failed.
    /// </remarks>
    public static IconElement? Create(string iconGlyphKey)
    {
        if (FluentIconCatalog.TryGetGlyph(iconGlyphKey, out var glyph))
        {
            return new FontIcon
            {
                Glyph = glyph,
                FontFamily = IconFont.Value,
                FontSize = Size,
            };
        }

        if (FluentIconCatalog.TryGetPathData(iconGlyphKey, out var pathData))
        {
            // Not tolerated as a null return. The catalog claims to hold this icon, so a failed
            // parse is a defect in the catalog, and swallowing it reproduces the exact bug this
            // method's remarks describe: a blank space in the pane and nothing in any log.
            var geometry = IconGeometry.FromPathData(pathData)
                ?? throw new InvalidOperationException(
                    $"Icon '{iconGlyphKey}' has path data the XAML parser rejected.");

            // A PathIcon scales its data to the icon box, so the mark lands at the same optical
            // size as the font glyphs beside it without a hand-tuned transform.
            return new PathIcon { Data = geometry };
        }

        return null;
    }
}
