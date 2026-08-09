using System.Globalization;
using System.Xml.Linq;
using Winora.Core.Appearance;
using Xunit;

namespace Winora.Architecture.Tests;

/// <summary>
/// <c>Palette.xaml</c> must hold exactly what <see cref="SchemeDerivation" /> produces for the
/// default preset.
/// </summary>
/// <remarks>
/// <para>
/// This suite used to measure the palette's own literals against the WCAG floors, compositing
/// Winora's translucent sheet over an assumed Mica base — approximate by necessity, because the app
/// cannot read the user's wallpaper tint. Both halves of that have changed. The colours the app
/// paints with are now derived at runtime from the two the user picked, and the floors are enforced
/// over every shipped preset by <c>ColorSchemePresetsTests</c> using the same arithmetic. Repeating
/// the measurement here would only re-measure a copy.
/// </para>
/// <para>
/// What is left is the thing that can still go wrong: the literals in the XAML exist so a page
/// rendered before <c>ThemeBrushService</c> runs already looks right, and nothing else would notice
/// if they drifted away from the derivation. A drift shows as a flash of the wrong colour at
/// startup, on someone else's machine, once. So they are re-derived and compared exactly.
/// </para>
/// <para>
/// Reading the XAML as text is still deliberate: loading the dictionary needs WinUI activation,
/// which a plain test host does not have. High Contrast is excluded because that dictionary hands
/// everything to the system's own colours, which Winora does not choose and must not measure.
/// </para>
/// </remarks>
public sealed class PaletteContrastTests
{
    /// <summary>WCAG 2.1 SC 1.4.11 for a meaningful non-text mark, here the Discord logo.</summary>
    private const double NonTextFloor = 3.0;

    private static readonly string PalettePath = Path.Combine(
        RepositoryRoot(), "src", "Winora.App", "Resources", "Styles", "Palette.xaml");

    /// <summary>
    /// Which theme dictionary carries which preset. <c>Default</c> is the scheme a fresh install
    /// gets; <c>Light</c> is its counterpart for when the canvas is light.
    /// </summary>
    public static TheoryData<string, string> ThemeToPreset() => new()
    {
        { "Default", "white-dark" },
        { "Light", "white-light" },
    };

    /// <summary>Every brush key that must equal a derived value, and which one.</summary>
    private static IReadOnlyDictionary<string, Func<DerivedPalette, ColorValue>> DerivedKeys { get; } =
        new Dictionary<string, Func<DerivedPalette, ColorValue>>(StringComparer.Ordinal)
        {
            ["WinoraCanvasBrush"] = static p => p.Canvas,
            ["WinoraContentSurfaceBrush"] = static p => p.Sheet,
            ["WinoraContentSurfaceStroke"] = static p => p.SheetStroke,
            ["WinoraCardFill"] = static p => p.Card,
            ["WinoraCardFillHover"] = static p => p.CardHover,
            ["WinoraCardStroke"] = static p => p.Divider,
            ["WinoraDividerBrush"] = static p => p.Divider,
            ["WinoraStrokeBrush"] = static p => p.Stroke,
            ["WinoraHoverBrush"] = static p => p.Hover,

            ["WinoraWashPersonalization"] = static p => p.Card,
            ["WinoraWashMaintenance"] = static p => p.Card,
            ["WinoraWashSystem"] = static p => p.Card,

            ["WinoraTextPrimary"] = static p => p.TextPrimary,
            ["WinoraTextSecondary"] = static p => p.TextSecondary,
            ["WinoraTextMuted"] = static p => p.TextMuted,
            ["WinoraTextFaint"] = static p => p.TextFaint,

            ["WinoraHuePersonalization"] = static p => p.Accent,
            ["WinoraHueMaintenance"] = static p => p.Accent,
            ["WinoraHueSystem"] = static p => p.Accent,
            ["WinoraOnAccentBrush"] = static p => p.OnAccent,
            ["WinoraNavSelectedBrush"] = static p => p.AccentSoft,

            ["ToggleSwitchFillOn"] = static p => p.Accent,
            ["ToggleSwitchFillOnPointerOver"] = static p => p.AccentHover,
            ["ToggleSwitchFillOnPressed"] = static p => p.AccentPressed,
            ["ToggleSwitchStrokeOn"] = static p => p.Accent,
            ["ToggleSwitchStrokeOnPointerOver"] = static p => p.AccentHover,
            ["ToggleSwitchStrokeOnPressed"] = static p => p.AccentPressed,
            ["ToggleSwitchKnobFillOn"] = static p => p.OnAccent,
            ["ToggleSwitchKnobFillOnPointerOver"] = static p => p.OnAccent,
            ["ToggleSwitchKnobFillOnPressed"] = static p => p.OnAccent,

            ["SliderTrackValueFill"] = static p => p.Accent,
            ["SliderTrackValueFillPointerOver"] = static p => p.AccentHover,
            ["SliderTrackValueFillPressed"] = static p => p.AccentPressed,
            ["SliderThumbBackground"] = static p => p.Accent,
            ["SliderThumbBackgroundPointerOver"] = static p => p.AccentHover,
            ["SliderThumbBackgroundPressed"] = static p => p.AccentPressed,
        };

    [Theory]
    [MemberData(nameof(ThemeToPreset))]
    public void Every_literal_matches_the_derived_default_preset(string theme, string presetId)
    {
        var palette = Palette.Load(PalettePath, theme);
        var derived = SchemeDerivation.Derive(ColorSchemePresets.Require(presetId).Scheme);

        var drifted = new List<string>();
        foreach (var (key, select) in DerivedKeys)
        {
            var expected = select(derived);
            var actual = palette.Colour(key);
            if (actual != expected)
            {
                drifted.Add($"{key}: XAML has {actual.ToHex()}, derivation gives {expected.ToHex()}");
            }
        }

        Assert.True(
            drifted.Count == 0,
            $"{theme} has drifted from the '{presetId}' preset:\n  " + string.Join("\n  ", drifted));
    }

    /// <summary>
    /// The surfaces are opaque now that the window is a flat canvas.
    /// </summary>
    /// <remarks>
    /// A leftover <c>Opacity</c> would darken or lighten a colour after it had been measured, so
    /// every reported ratio would be right about a colour the screen never shows. The accent edge is
    /// the one exception and it is checked separately below.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ThemeToPreset))]
    public void No_painted_brush_carries_an_opacity(string theme, string presetId)
    {
        _ = presetId;
        var palette = Palette.Load(PalettePath, theme);

        var translucent = DerivedKeys.Keys
            .Where(key => palette.Opacity(key) is not 1.0)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            translucent.Length == 0,
            $"{theme} still has translucent brushes: " + string.Join(", ", translucent));
    }

    /// <summary>
    /// The primary button's outline is present or absent by opacity, not by colour, so that the
    /// brush keeps a usable value for the moment an accent needs an edge again.
    /// </summary>
    [Theory]
    [MemberData(nameof(ThemeToPreset))]
    public void The_accent_edge_is_hidden_by_opacity_and_coloured_like_a_stroke(string theme, string presetId)
    {
        var palette = Palette.Load(PalettePath, theme);
        var derived = SchemeDerivation.Derive(ColorSchemePresets.Require(presetId).Scheme);

        Assert.Equal(derived.Stroke, palette.Colour("WinoraAccentEdgeBrush"));

        // The default preset's accent stands well clear of the sheet, so it ships hidden.
        Assert.Null(derived.AccentEdge);
        Assert.Equal(0.0, palette.Opacity("WinoraAccentEdgeBrush"));
    }

    /// <summary>
    /// The Discord mark is the one colour in the palette that is not derived: it is that product's
    /// own, so it is fixed, and it therefore still needs measuring on its own.
    /// </summary>
    [Theory]
    [MemberData(nameof(ThemeToPreset))]
    public void The_community_glyph_clears_the_non_text_floor(string theme, string presetId)
    {
        var palette = Palette.Load(PalettePath, theme);
        var derived = SchemeDerivation.Derive(ColorSchemePresets.Require(presetId).Scheme);

        var glyph = palette.Colour("WinoraCommunityGlyphBrush");
        var ratio = ContrastMath.Ratio(glyph, derived.CardHover);

        Assert.True(
            ratio >= NonTextFloor,
            $"{theme}: the community glyph is {glyph.ToHex()} on a hovered card " +
            $"{derived.CardHover.ToHex()} — {ratio:F2}:1, below the {NonTextFloor:F1}:1 floor.");
    }

    /// <summary>
    /// A theme that carries a tone for secondary text must carry every tier, so a screen never has
    /// to invent one. Dropping a tier is how an unmeasured colour gets written inline in a page.
    /// </summary>
    [Theory]
    [MemberData(nameof(ThemeToPreset))]
    public void All_four_text_tiers_exist_and_differ(string theme, string presetId)
    {
        _ = presetId;
        var palette = Palette.Load(PalettePath, theme);

        var tones = new[]
        {
            palette.Colour("WinoraTextPrimary"),
            palette.Colour("WinoraTextSecondary"),
            palette.Colour("WinoraTextMuted"),
            palette.Colour("WinoraTextFaint"),
        };

        Assert.Equal(tones.Length, tones.Distinct().Count());
    }

    /// <summary>
    /// The two themes must not share a text tone. One grey cannot clear the floor against a
    /// near-black and a near-white surface at once, and sharing the literal is what caused that.
    /// </summary>
    [Theory]
    [InlineData("WinoraTextMuted")]
    [InlineData("WinoraTextFaint")]
    public void A_text_tone_is_not_reused_across_the_two_themes(string brushKey) =>
        Assert.NotEqual(
            Palette.Load(PalettePath, "Default").Colour(brushKey),
            Palette.Load(PalettePath, "Light").Colour(brushKey));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "src", "Winora.Core", "Winora.Core.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>One theme dictionary out of Palette.xaml, read as text.</summary>
    private sealed class Palette
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private readonly Dictionary<string, XElement> _brushes;
        private readonly string _theme;

        private Palette(string theme, Dictionary<string, XElement> brushes)
        {
            _theme = theme;
            _brushes = brushes;
        }

        internal static Palette Load(string path, string theme)
        {
            var dictionary = XDocument.Load(path)
                .Descendants(Xaml + "ResourceDictionary")
                .SingleOrDefault(element =>
                    string.Equals((string?)element.Attribute(X + "Key"), theme, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Palette.xaml has no '{theme}' theme.");

            var brushes = dictionary
                .Elements(Xaml + "SolidColorBrush")
                .Where(static element => element.Attribute(X + "Key") is not null)
                .ToDictionary(
                    static element => (string)element.Attribute(X + "Key")!,
                    static element => element,
                    StringComparer.Ordinal);

            return new Palette(theme, brushes);
        }

        internal ColorValue Colour(string key)
        {
            var value = (string?)Element(key).Attribute("Color")
                ?? throw new InvalidOperationException($"'{key}' in theme '{_theme}' has no Color.");

            // A ThemeResource reference belongs to High Contrast, which this suite does not read.
            Assert.StartsWith("#", value, StringComparison.Ordinal);
            return ColorValue.Parse(value);
        }

        internal double Opacity(string key) =>
            Element(key).Attribute("Opacity") is { } opacity
                ? double.Parse(opacity.Value, CultureInfo.InvariantCulture)
                : 1.0;

        private XElement Element(string key) =>
            _brushes.TryGetValue(key, out var element)
                ? element
                : throw new InvalidOperationException(
                    $"'{key}' is not a SolidColorBrush in theme '{_theme}'.");
    }
}
