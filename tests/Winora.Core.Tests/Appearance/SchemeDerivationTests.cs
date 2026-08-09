using Winora.Core.Appearance;
using Xunit;

namespace Winora.Core.Tests.Appearance;

/// <summary>
/// Two chosen colours in, a whole palette out.
/// </summary>
/// <remarks>
/// The user picks a background and an accent; everything else — text tiers, sheet, card, hovered
/// card, divider, stroke, the colour printed on the accent — is derived. That is a deliberate limit
/// rather than a missing feature: a scheme assembled from two decisions cannot become unreadable in
/// a way the person did not ask for, and one assembled from twelve can. Every derived value stays
/// overridable, so the ceiling is a default and not a wall.
/// </remarks>
public sealed class SchemeDerivationTests
{
    private static readonly ColorValue NearBlack = ColorValue.Parse("#0C0C0F");
    private static readonly ColorValue NearWhite = ColorValue.Parse("#F3F3F5");
    private static readonly ColorValue Violet = ColorValue.Parse("#A78BFA");

    private static WinoraColorScheme Scheme(ColorValue canvas, ColorValue accent) =>
        new() { Canvas = canvas, Accent = accent };

    [Fact]
    public void A_dark_canvas_produces_light_ink()
    {
        var palette = SchemeDerivation.Derive(Scheme(NearBlack, Violet));

        Assert.True(palette.IsDark);
        Assert.True(
            ContrastMath.RelativeLuminance(palette.TextPrimary) > 0.5,
            "Primary text on a near-black canvas must be light.");
    }

    [Fact]
    public void A_light_canvas_produces_dark_ink()
    {
        var palette = SchemeDerivation.Derive(Scheme(NearWhite, Violet));

        Assert.False(palette.IsDark);
        Assert.True(
            ContrastMath.RelativeLuminance(palette.TextPrimary) < 0.1,
            "Primary text on a near-white canvas must be dark.");
    }

    /// <summary>
    /// The three secondary tiers must stay ordered and distinct. Collapsing two of them is how a
    /// screen ends up with no way to say "this is less important" and starts inventing a colour.
    /// </summary>
    [Theory]
    [InlineData("#0C0C0F")]
    [InlineData("#F3F3F5")]
    public void The_text_tiers_step_away_from_the_ink_in_order(string canvasHex)
    {
        var palette = SchemeDerivation.Derive(Scheme(ColorValue.Parse(canvasHex), Violet));
        var canvas = ColorValue.Parse(canvasHex);

        var primary = ContrastMath.Ratio(palette.TextPrimary, canvas);
        var muted = ContrastMath.Ratio(palette.TextMuted, canvas);
        var faint = ContrastMath.Ratio(palette.TextFaint, canvas);

        Assert.True(
            primary > muted && muted > faint,
            $"primary {primary:F2}, muted {muted:F2}, faint {faint:F2} — each tier must be quieter than the last.");
    }

    /// <summary>
    /// Whichever of black and white reads better on the accent is the one printed on it. Hard-coding
    /// white here is the classic defect: it works for every accent until someone picks a pale one.
    /// </summary>
    [Theory]
    [InlineData("#FFFCFC", false)]
    [InlineData("#E0D7AF", false)]
    [InlineData("#0C0C0F", true)]
    [InlineData("#6D3FD4", true)]
    public void The_colour_on_the_accent_is_the_more_contrasting_of_black_and_white(
        string accentHex,
        bool expectLight)
    {
        var palette = SchemeDerivation.Derive(Scheme(NearBlack, ColorValue.Parse(accentHex)));
        var isLight = ContrastMath.RelativeLuminance(palette.OnAccent) > 0.5;

        Assert.Equal(expectLight, isLight);
    }

    [Fact]
    public void An_explicit_colour_on_the_accent_wins_over_the_derived_one()
    {
        var chosen = ColorValue.Parse("#123456");
        var palette = SchemeDerivation.Derive(new WinoraColorScheme
        {
            Canvas = NearBlack,
            Accent = Violet,
            OnAccent = chosen,
        });

        Assert.Equal(chosen, palette.OnAccent);
    }

    [Fact]
    public void Every_overridable_value_wins_over_the_derived_one()
    {
        var chosen = ColorValue.Parse("#123456");
        var palette = SchemeDerivation.Derive(new WinoraColorScheme
        {
            Canvas = NearBlack,
            Accent = Violet,
            TextPrimary = chosen,
            TextMuted = chosen,
            TextFaint = chosen,
            Card = chosen,
            CardHover = chosen,
            Divider = chosen,
            Stroke = chosen,
        });

        Assert.Equal(chosen, palette.TextPrimary);
        Assert.Equal(chosen, palette.TextMuted);
        Assert.Equal(chosen, palette.TextFaint);
        Assert.Equal(chosen, palette.Card);
        Assert.Equal(chosen, palette.CardHover);
        Assert.Equal(chosen, palette.Divider);
        Assert.Equal(chosen, palette.Stroke);
    }

    /// <summary>
    /// A hovered card is the lightest surface a dark theme can put behind text and the darkest a
    /// light theme can. It is the state hand-checking misses, so the derivation has to keep it on
    /// the same side of the sheet as the resting card rather than crossing over.
    /// </summary>
    [Theory]
    [InlineData("#0C0C0F")]
    [InlineData("#F3F3F5")]
    public void A_hovered_card_is_a_further_step_than_a_resting_card(string canvasHex)
    {
        var palette = SchemeDerivation.Derive(Scheme(ColorValue.Parse(canvasHex), Violet));

        var restingStep = ContrastMath.Ratio(palette.Card, palette.Sheet);
        var hoverStep = ContrastMath.Ratio(palette.CardHover, palette.Sheet);

        Assert.True(hoverStep > restingStep, $"resting {restingStep:F3}, hover {hoverStep:F3}");
    }

    /// <summary>
    /// An accent that all but matches the canvas stops reading as a filled control. Rather than
    /// refuse the choice, the derivation hands back an edge so the button still has a boundary —
    /// the graphite scheme is exactly this case and it is a legitimate thing to want.
    /// </summary>
    [Fact]
    public void An_accent_too_close_to_the_sheet_gets_an_edge()
    {
        var palette = SchemeDerivation.Derive(Scheme(NearBlack, ColorValue.Parse("#08080A")));
        Assert.NotNull(palette.AccentEdge);
    }

    /// <summary>
    /// Hover and pressed have to stay ordered between the accent and the surface it sits on, in
    /// both themes. Lightening for hover is the obvious implementation and it inverts in a light
    /// scheme, which is why the derivation lets the surface through instead.
    /// </summary>
    [Theory]
    [InlineData("#0C0C0F")]
    [InlineData("#F3F3F5")]
    public void Hover_and_pressed_step_from_the_accent_toward_the_surface(string canvasHex)
    {
        var palette = SchemeDerivation.Derive(Scheme(ColorValue.Parse(canvasHex), Violet));

        var accentToSheet = ContrastMath.Ratio(palette.Accent, palette.Sheet);
        var hoverToSheet = ContrastMath.Ratio(palette.AccentHover, palette.Sheet);
        var pressedToSheet = ContrastMath.Ratio(palette.AccentPressed, palette.Sheet);

        Assert.True(
            accentToSheet > hoverToSheet && hoverToSheet > pressedToSheet,
            $"accent {accentToSheet:F2}, hover {hoverToSheet:F2}, pressed {pressedToSheet:F2}");
    }

    [Fact]
    public void An_accent_that_stands_out_needs_no_edge()
    {
        var palette = SchemeDerivation.Derive(Scheme(NearBlack, ColorValue.Parse("#FFFCFC")));
        Assert.Null(palette.AccentEdge);
    }
}
