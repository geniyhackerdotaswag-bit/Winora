namespace Winora.Core.Appearance;

/// <summary>Whether a check is measuring text or a non-text mark, which sets its floor.</summary>
public enum ContrastRole
{
    Text,
    NonText,
}

/// <summary>One measured pair from a scheme.</summary>
/// <param name="Id">
/// A stable identifier the UI turns into a localized label. Never a display string: the report is
/// shown to a person and the identifiers outlive any wording.
/// </param>
public sealed record ContrastCheck(
    string Id,
    ColorValue Foreground,
    ColorValue Surface,
    ContrastRole Role)
{
    public double Floor =>
        Role is ContrastRole.Text ? SchemeContrast.TextFloor : SchemeContrast.NonTextFloor;

    public double Ratio => ContrastMath.Ratio(Foreground, Surface);

    public bool Passes => Ratio >= Floor;
}

/// <summary>What a scheme measures, and whether it may be applied.</summary>
public sealed record SchemeContrastReport(IReadOnlyList<ContrastCheck> Checks)
{
    public bool TextPasses => Checks.All(static check =>
        check.Role is not ContrastRole.Text || check.Passes);

    public bool NonTextPasses => Checks.All(static check =>
        check.Role is not ContrastRole.NonText || check.Passes);

    /// <summary>
    /// Only unreadable text blocks. A quiet accent is a taste someone is entitled to; body text
    /// below the floor is not a taste, and it cannot be undone from inside an app whose text nobody
    /// can read.
    /// </summary>
    public bool CanApply => TextPasses;
}

/// <summary>
/// Measures a palette against the WCAG floors, at runtime, on every edit.
/// </summary>
/// <remarks>
/// <para>
/// <c>PaletteContrastTests</c> holds the palette Winora ships to these same floors at build time. A
/// scheme a user assembles never passes through a build, so the identical measurement has to stand
/// in the editor — the app's rule is that contrast is measured and not judged, and that rule cannot
/// stop applying at the point where a human starts choosing the colours.
/// </para>
/// <para>
/// Both floors come from WCAG 2.1: 4.5:1 for normal-size text (SC 1.4.3) and 3:1 for meaningful
/// non-text marks such as the rule under a page title and the fill of a switch (SC 1.4.11). Holding
/// the non-text marks to the text floor would reject schemes that are in fact conformant, which is
/// the mirror image of the defect this guards against and just as wrong.
/// </para>
/// </remarks>
public static class SchemeContrast
{
    /// <summary>WCAG 2.1 SC 1.4.3, normal-size text.</summary>
    public const double TextFloor = 4.5;

    /// <summary>WCAG 2.1 SC 1.4.11, non-text contrast.</summary>
    public const double NonTextFloor = 3.0;

    public static SchemeContrastReport Measure(DerivedPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        // Every text tier is measured against the hovered card. It is the lightest surface a dark
        // scheme can produce and the darkest a light one can, so a tier that clears it clears every
        // other surface in the same scheme. Measuring the resting card instead is precisely the
        // omission that once shipped a tone reading 3.52:1 under the pointer.
        return new SchemeContrastReport(
        [
            new("text-primary", palette.TextPrimary, palette.CardHover, ContrastRole.Text),
            new("text-muted", palette.TextMuted, palette.CardHover, ContrastRole.Text),
            new("text-faint", palette.TextFaint, palette.CardHover, ContrastRole.Text),
            new("on-accent", palette.OnAccent, palette.Accent, ContrastRole.Text),

            new("accent-rule", palette.Accent, palette.Sheet, ContrastRole.NonText),
            new("accent-switch", palette.Accent, palette.CardHover, ContrastRole.NonText),
        ]);
    }
}
