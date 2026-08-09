using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Winora.Core.Appearance;

namespace Winora.App.Services;

/// <summary>Applies a colour scheme to the running app.</summary>
public interface IThemeBrushService
{
    /// <summary>
    /// Raised after a scheme has been applied.
    /// </summary>
    /// <remarks>
    /// Repainting the brushes covers everything drawn from them, but two things sit outside that
    /// system and have to be told: the element theme the shell requests, which is what makes stock
    /// controls resolve their own colours against the new canvas, and the caption buttons, which
    /// belong to the window rather than to the visual tree.
    /// </remarks>
    event EventHandler? Applied;

    /// <summary>The palette in force, derived from the scheme last applied.</summary>
    DerivedPalette Current { get; }

    /// <summary>The scheme last applied, including any preset association.</summary>
    WinoraColorScheme CurrentScheme { get; }

    /// <summary>
    /// True while Windows is in High Contrast, in which case nothing here paints anything.
    /// </summary>
    bool IsSuppressed { get; }

    /// <summary>
    /// Repaints every Winora brush from <paramref name="scheme" />, with no restart.
    /// </summary>
    /// <returns>
    /// The theme the shell should now request, so stock controls resolve their own colours against
    /// the same canvas. Null while suppressed.
    /// </returns>
    ElementTheme? Apply(WinoraColorScheme scheme);
}

/// <summary>
/// Repaints Winora's palette in place.
/// </summary>
/// <remarks>
/// <para>
/// The brushes in <c>Palette.xaml</c> are mutated rather than replaced. A <see cref="Brush" /> is a
/// reference, and every <c>{ThemeResource}</c> in the app holds the same instance, so changing
/// <see cref="SolidColorBrush.Color" /> repaints everything bound to it without rebuilding a single
/// page. Swapping the dictionary entry instead would leave every element still pointing at the old
/// brush — WinUI resolves those references once — and would produce a change that appears to work
/// because the first screen built afterwards looks right.
/// </para>
/// <para>
/// Both the Default and Light dictionaries are written with the same derived values, because the
/// scheme decides its own light or dark character from the canvas and the shell then requests the
/// matching <see cref="ElementTheme" />. Writing only the active one would leave the other holding
/// stale colours for the moment the element theme flips. High Contrast is never touched: that
/// dictionary belongs to the system, and <see cref="IHighContrastProbe" /> stops this service
/// entirely while it is in force.
/// </para>
/// </remarks>
public sealed class ThemeBrushService : IThemeBrushService
{
    /// <summary>Alpha of the header band at its strongest point.</summary>
    private const byte HeaderGradientAlpha = 0x26;

    /// <summary>
    /// The two dictionaries Winora owns. <c>HighContrast</c> is absent on purpose — that one
    /// belongs to the system, and overriding it is exactly how an app becomes unusable in it.
    /// </summary>
    private static readonly string[] PaintedThemes = ["Default", "Light"];

    private readonly IHighContrastProbe _highContrast;

    public ThemeBrushService(IHighContrastProbe highContrast)
    {
        _highContrast = highContrast ?? throw new ArgumentNullException(nameof(highContrast));

        CurrentScheme = ColorSchemePresets.Default;
        Current = SchemeDerivation.Derive(CurrentScheme);
        IsSuppressed = _highContrast.IsHighContrast();
    }

    /// <inheritdoc />
    public event EventHandler? Applied;

    /// <inheritdoc />
    public DerivedPalette Current { get; private set; }

    /// <inheritdoc />
    public WinoraColorScheme CurrentScheme { get; private set; }

    /// <inheritdoc />
    public bool IsSuppressed { get; private set; }

    /// <inheritdoc />
    public ElementTheme? Apply(WinoraColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        CurrentScheme = scheme;
        Current = SchemeDerivation.Derive(scheme);

        // Re-read every time rather than caching: a user can turn High Contrast on while Winora is
        // open, and the next thing this service does must be nothing.
        IsSuppressed = _highContrast.IsHighContrast();
        if (IsSuppressed)
        {
            Applied?.Invoke(this, EventArgs.Empty);
            return null;
        }

        foreach (var dictionary in ThemeDictionaries())
        {
            Paint(dictionary, Current);
        }

        Applied?.Invoke(this, EventArgs.Empty);
        return Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;
    }

    private static void Paint(ResourceDictionary dictionary, DerivedPalette palette)
    {
        // Winora's own surfaces.
        Set(dictionary, "WinoraCanvasBrush", palette.Canvas);
        Set(dictionary, "WinoraContentSurfaceBrush", palette.Sheet);
        Set(dictionary, "WinoraContentSurfaceStroke", palette.SheetStroke);
        Set(dictionary, "WinoraCardFill", palette.Card);
        Set(dictionary, "WinoraCardFillHover", palette.CardHover);
        Set(dictionary, "WinoraCardStroke", palette.Divider);
        Set(dictionary, "WinoraDividerBrush", palette.Divider);
        Set(dictionary, "WinoraStrokeBrush", palette.Stroke);
        Set(dictionary, "WinoraHoverBrush", palette.Hover);

        // The card washes are neutral by design: a surface is a surface, not a category.
        Set(dictionary, "WinoraWashPersonalization", palette.Card);
        Set(dictionary, "WinoraWashMaintenance", palette.Card);
        Set(dictionary, "WinoraWashSystem", palette.Card);

        // Text.
        Set(dictionary, "WinoraTextPrimary", palette.TextPrimary);
        Set(dictionary, "WinoraTextSecondary", palette.TextSecondary);
        Set(dictionary, "WinoraTextMuted", palette.TextMuted);
        Set(dictionary, "WinoraTextFaint", palette.TextFaint);

        // The accent. All three hue keys resolve to it; they survive only to mark the places where
        // colour is allowed to appear at all, so reintroducing a second one stays a deliberate edit.
        Set(dictionary, "WinoraHuePersonalization", palette.Accent);
        Set(dictionary, "WinoraHueMaintenance", palette.Accent);
        Set(dictionary, "WinoraHueSystem", palette.Accent);
        Set(dictionary, "WinoraOnAccentBrush", palette.OnAccent);
        Set(dictionary, "WinoraNavSelectedBrush", palette.AccentSoft);

        // The edge is either there or it is not, and "not" has to be invisible rather than black.
        // Opacity carries that, because the brush itself must keep a usable colour for the moment
        // the accent changes back to one that needs an outline.
        Set(
            dictionary,
            "WinoraAccentEdgeBrush",
            palette.AccentEdge ?? palette.Stroke,
            palette.AccentEdge is null ? 0.0 : 1.0);

        // Switches and sliders take Winora's accent rather than the desktop's.
        Set(dictionary, "ToggleSwitchFillOn", palette.Accent);
        Set(dictionary, "ToggleSwitchFillOnPointerOver", palette.AccentHover);
        Set(dictionary, "ToggleSwitchFillOnPressed", palette.AccentPressed);
        Set(dictionary, "ToggleSwitchStrokeOn", palette.Accent);
        Set(dictionary, "ToggleSwitchStrokeOnPointerOver", palette.AccentHover);
        Set(dictionary, "ToggleSwitchStrokeOnPressed", palette.AccentPressed);
        Set(dictionary, "ToggleSwitchKnobFillOn", palette.OnAccent);
        Set(dictionary, "ToggleSwitchKnobFillOnPointerOver", palette.OnAccent);
        Set(dictionary, "ToggleSwitchKnobFillOnPressed", palette.OnAccent);

        Set(dictionary, "SliderTrackValueFill", palette.Accent);
        Set(dictionary, "SliderTrackValueFillPointerOver", palette.AccentHover);
        Set(dictionary, "SliderTrackValueFillPressed", palette.AccentPressed);
        Set(dictionary, "SliderThumbBackground", palette.Accent);
        Set(dictionary, "SliderThumbBackgroundPointerOver", palette.AccentHover);
        Set(dictionary, "SliderThumbBackgroundPressed", palette.AccentPressed);

        // One header band, deliberately identical on every page. Three different ones read as three
        // different apps.
        SetGradient(dictionary, "WinoraHeaderPersonalization", palette.Accent);
        SetGradient(dictionary, "WinoraHeaderMaintenance", palette.Accent);
        SetGradient(dictionary, "WinoraHeaderSystem", palette.Accent);
    }

    /// <summary>
    /// Every Default and Light dictionary merged into the application, wherever it was merged from.
    /// </summary>
    /// <remarks>
    /// Walked rather than reaching for <c>Palette.xaml</c> by name, because the navigation styles
    /// carry their own themed entries and a second dictionary added later would otherwise be missed
    /// silently — the app would simply keep one stale colour with nothing to explain it.
    /// </remarks>
    private static IEnumerable<ResourceDictionary> ThemeDictionaries()
    {
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            foreach (var theme in PaintedThemes)
            {
                if (merged.ThemeDictionaries.TryGetValue(theme, out var value) &&
                    value is ResourceDictionary dictionary)
                {
                    yield return dictionary;
                }
            }
        }
    }

    /// <summary>
    /// Repaints one brush, and does nothing where the key is absent.
    /// </summary>
    /// <remarks>
    /// Absence is legitimate here: the stock <c>Slider</c> and <c>ToggleSwitch</c> keys are only
    /// present once a theme dictionary declares them, and Winora declares the ones it means to
    /// control. The keys it owns are covered by <c>ThemeDictionaryTests</c>, which fails when the
    /// two themes stop declaring the same set.
    /// </remarks>
    private static void Set(
        ResourceDictionary dictionary,
        string key,
        ColorValue colour,
        double opacity = 1.0)
    {
        if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = ToColor(colour);

            // Set explicitly, because opacity belonged to the translucent era. Every surface is
            // opaque now that the window is a flat canvas, and a stale 0.62 left on a brush would
            // quietly darken a colour the contrast report has already declared safe.
            brush.Opacity = opacity;
        }
    }

    private static void SetGradient(ResourceDictionary dictionary, string key, ColorValue accent)
    {
        if (dictionary.TryGetValue(key, out var value) &&
            value is LinearGradientBrush { GradientStops.Count: >= 2 } gradient)
        {
            gradient.GradientStops[0].Color = ToColor(accent, HeaderGradientAlpha);
            gradient.GradientStops[^1].Color = ToColor(accent, 0x00);
        }
    }

    private static Color ToColor(ColorValue colour, byte alpha = 0xFF) =>
        Color.FromArgb(alpha, colour.R, colour.G, colour.B);
}
