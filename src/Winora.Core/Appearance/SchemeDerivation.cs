namespace Winora.Core.Appearance;

/// <summary>
/// Turns the two colours a user chose into the whole palette.
/// </summary>
/// <remarks>
/// <para>
/// Every surface is the canvas moved a fixed fraction toward the ink — the near-white used for text
/// on a dark canvas, the near-black used on a light one. So surfaces lighten in a dark scheme and
/// darken in a light one. That is the opposite of the usual "elevation is lighter" rule and it is
/// chosen deliberately: stepping toward the ink always has headroom, whereas stepping toward white
/// runs out of room the moment someone picks a canvas that is already near white, and collapses the
/// sheet, the card and the hovered card onto one another.
/// </para>
/// <para>
/// The two themes do not share their text fractions. Because luminance is not linear in the channel
/// value, the same step produces far less perceived contrast against a near-white surface than
/// against a near-black one — which is the measured reason a single grey once failed the floor in
/// both themes at once. The light figures are larger for that reason and for no other.
/// </para>
/// </remarks>
public static class SchemeDerivation
{
    /// <summary>
    /// Above this relative luminance the canvas is treated as light and takes dark ink.
    /// </summary>
    /// <remarks>
    /// Set below the midpoint because a mid-grey canvas is unreadable either way, and the exact
    /// crossing point therefore decides nothing that matters: <see cref="SchemeContrast" /> refuses
    /// the whole region regardless of which side of this line it falls on.
    /// </remarks>
    private const double DarkCanvasCeiling = 0.18;

    /// <summary>Winora's near-white. Slightly warm, so it does not read as clinical against black.</summary>
    private static readonly ColorValue LightInk = new(0xFF, 0xFC, 0xFC);

    private static readonly ColorValue DarkInk = new(0x0C, 0x0C, 0x0F);

    // Surfaces. Shared by both themes: the asymmetry that matters is in the text tiers below.
    private const double SheetStep = 0.02;
    private const double SheetStrokeStep = 0.12;
    private const double CardStep = 0.055;
    private const double CardHoverStep = 0.095;
    private const double DividerStep = 0.10;
    private const double HoverStep = 0.09;
    private const double StrokeStep = 0.24;

    // Text tiers, per theme. See the remarks on the type for why these differ.
    private const double SecondaryStepDark = 0.80;
    private const double SecondaryStepLight = 0.84;
    private const double MutedStepDark = 0.70;
    private const double MutedStepLight = 0.76;
    private const double FaintStepDark = 0.58;

    /// <summary>
    /// Measured, not guessed. 0.64 lands the light theme's faint tone at 4.51:1 on a hovered card —
    /// over the floor by one hundredth, which is not a margin, it is a coincidence. 0.68 puts it at
    /// 5.19:1, level with the dark theme's headroom.
    /// </summary>
    private const double FaintStepLight = 0.68;

    private const double AccentSoftStepDark = 0.16;
    private const double AccentSoftStepLight = 0.14;

    // How much of the surface shows through the accent in each interaction state, mirroring the
    // proportions WinUI uses for its own accent fills.
    private const double AccentHoverOpacity = 0.90;
    private const double AccentPressedOpacity = 0.80;

    /// <summary>
    /// Below this ratio against the sheet, a filled control is not distinguishable from the surface
    /// behind it and is given an outline instead.
    /// </summary>
    private const double AccentNeedsEdgeBelow = 1.6;

    public static DerivedPalette Derive(WinoraColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        var canvas = scheme.Canvas;
        var isDark = ContrastMath.RelativeLuminance(canvas) < DarkCanvasCeiling;
        var ink = isDark ? LightInk : DarkInk;

        var sheet = ContrastMath.Blend(canvas, ink, SheetStep);
        var card = scheme.Card ?? ContrastMath.Blend(sheet, ink, CardStep);
        var cardHover = scheme.CardHover ?? ContrastMath.Blend(sheet, ink, CardHoverStep);
        var stroke = scheme.Stroke ?? ContrastMath.Blend(canvas, ink, StrokeStep);
        var accent = scheme.Accent;

        return new DerivedPalette
        {
            IsDark = isDark,
            Canvas = canvas,
            Sheet = sheet,
            SheetStroke = ContrastMath.Blend(canvas, ink, SheetStrokeStep),
            Card = card,
            CardHover = cardHover,
            Divider = scheme.Divider ?? ContrastMath.Blend(canvas, ink, DividerStep),
            Stroke = stroke,
            Hover = ContrastMath.Blend(canvas, ink, HoverStep),

            TextPrimary = scheme.TextPrimary ?? ink,
            TextSecondary = ContrastMath.Blend(
                canvas,
                ink,
                isDark ? SecondaryStepDark : SecondaryStepLight),
            TextMuted = scheme.TextMuted ?? ContrastMath.Blend(
                canvas,
                ink,
                isDark ? MutedStepDark : MutedStepLight),
            TextFaint = scheme.TextFaint ?? ContrastMath.Blend(
                canvas,
                ink,
                isDark ? FaintStepDark : FaintStepLight),

            Accent = accent,
            OnAccent = scheme.OnAccent ?? MostLegibleOn(accent),
            AccentHover = ContrastMath.Blend(sheet, accent, AccentHoverOpacity),
            AccentPressed = ContrastMath.Blend(sheet, accent, AccentPressedOpacity),
            AccentSoft = ContrastMath.Blend(
                sheet,
                accent,
                isDark ? AccentSoftStepDark : AccentSoftStepLight),
            AccentEdge = ContrastMath.Ratio(accent, sheet) < AccentNeedsEdgeBelow ? stroke : null,
        };
    }

    /// <summary>
    /// Whichever of black and white reads better on the accent.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed. Hard-coding white here works for every accent until someone
    /// picks a pale one, at which point the primary button's label disappears into its own fill.
    /// </remarks>
    private static ColorValue MostLegibleOn(ColorValue accent) =>
        ContrastMath.Ratio(LightInk, accent) >= ContrastMath.Ratio(DarkInk, accent)
            ? LightInk
            : DarkInk;
}
