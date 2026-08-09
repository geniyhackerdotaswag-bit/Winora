namespace Winora.Core.Appearance;

/// <summary>
/// WCAG 2.1 relative luminance and contrast, plus the one blend every surface is built from.
/// </summary>
/// <remarks>
/// <para>
/// These formulas are fixed by the specification and are not Winora's to tune. They live in Core so
/// that the build-time palette suite, the runtime scheme editor and the validator that decides
/// whether a scheme may be applied all measure with the same code. They used to exist only as a
/// private copy inside the architecture tests, which was fine while the palette was a literal in a
/// XAML file and became untenable the moment a user could compose one.
/// </para>
/// <para>
/// See <see href="https://www.w3.org/TR/WCAG21/#dfn-relative-luminance" /> and
/// <see href="https://www.w3.org/TR/WCAG21/#dfn-contrast-ratio" />.
/// </para>
/// </remarks>
public static class ContrastMath
{
    /// <summary>Relative luminance, 0 for black and 1 for white.</summary>
    public static double RelativeLuminance(ColorValue colour) =>
        (0.2126 * Linear(colour.R)) + (0.7152 * Linear(colour.G)) + (0.0722 * Linear(colour.B));

    /// <summary>
    /// The contrast ratio between two colours, from 1:1 to 21:1. Order does not matter — the
    /// brighter of the two always becomes the numerator.
    /// </summary>
    public static double Ratio(ColorValue a, ColorValue b)
    {
        var first = RelativeLuminance(a);
        var second = RelativeLuminance(b);
        var (high, low) = first >= second ? (first, second) : (second, first);

        return (high + 0.05) / (low + 0.05);
    }

    /// <summary>
    /// Moves <paramref name="baseColour" /> a fraction of the way toward <paramref name="toward" />.
    /// </summary>
    /// <remarks>
    /// This is also exactly what compositing a translucent layer over an opaque one does, with
    /// <paramref name="amount" /> as the alpha. Keeping it as one function rather than two means the
    /// two uses cannot drift apart, which matters because a surface derived one way and measured the
    /// other would report a contrast the screen does not actually show.
    /// </remarks>
    /// <param name="amount">Between 0 and 1 inclusive.</param>
    public static ColorValue Blend(ColorValue baseColour, ColorValue toward, double amount)
    {
        if (amount is < 0.0 or > 1.0 || double.IsNaN(amount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                "A blend amount must be between 0 and 1.");
        }

        return new ColorValue(
            Channel(baseColour.R, toward.R, amount),
            Channel(baseColour.G, toward.G, amount),
            Channel(baseColour.B, toward.B, amount));
    }

    private static byte Channel(byte from, byte to, double amount) =>
        (byte)Math.Round(from + ((to - from) * amount), MidpointRounding.AwayFromZero);

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
