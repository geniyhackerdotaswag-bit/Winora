using Winora.Core.Appearance;
using Xunit;

namespace Winora.Core.Tests.Appearance;

/// <summary>
/// Contrast is measured, not judged — now at runtime, because the scheme is composed at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <c>PaletteContrastTests</c> holds the shipped palette to the floor at build time. A scheme the
/// user assembles never passes through a build, so the same measurement has to stand in the editor
/// and refuse to apply an unreadable one. The asymmetry between the two floors is deliberate and
/// comes from WCAG: 4.5:1 for normal text (SC 1.4.3), 3:1 for meaningful non-text marks such as the
/// rule under a page title and the fill of a switch (SC 1.4.11). Holding the rule to the text floor
/// would reject schemes that are in fact conformant.
/// </para>
/// <para>
/// Only the text failures block. A quiet accent is a legitimate taste; unreadable body text is not
/// a taste, and it cannot be undone from inside an app whose text cannot be read.
/// </para>
/// </remarks>
public sealed class SchemeContrastTests
{
    private static SchemeContrastReport Measure(string canvas, string accent) =>
        SchemeContrast.Measure(SchemeDerivation.Derive(new WinoraColorScheme
        {
            Canvas = ColorValue.Parse(canvas),
            Accent = ColorValue.Parse(accent),
        }));

    [Fact]
    public void The_default_scheme_passes_everything()
    {
        var report = Measure("#0C0C0F", "#FFFCFC");

        Assert.True(report.TextPasses, Explain(report));
        Assert.True(report.NonTextPasses, Explain(report));
        Assert.True(report.CanApply);
    }

    /// <summary>
    /// A mid-grey canvas is the genuinely hard case: neither light nor dark ink clears the floor
    /// against it, and it is exactly what someone lands on while dragging a colour picker.
    /// </summary>
    [Fact]
    public void A_mid_grey_canvas_fails_the_text_floor_and_cannot_be_applied()
    {
        var report = Measure("#7A7A7A", "#FFFCFC");

        Assert.False(report.TextPasses);
        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, static check => !check.Passes && check.Floor == SchemeContrast.TextFloor);
    }

    /// <summary>
    /// Graphite on near-black: the text is fine, the accent all but disappears. That is a warning
    /// and not a refusal, and getting this backwards would either block a scheme someone wants or
    /// ship one nobody can read.
    /// </summary>
    [Fact]
    public void A_near_invisible_accent_warns_but_still_applies()
    {
        var report = Measure("#0C0C0F", "#08080A");

        Assert.True(report.TextPasses, Explain(report));
        Assert.False(report.NonTextPasses);
        Assert.True(report.CanApply);
    }

    [Fact]
    public void Every_check_names_a_stable_identifier()
    {
        var report = Measure("#0C0C0F", "#A78BFA");

        Assert.NotEmpty(report.Checks);
        Assert.All(report.Checks, static check => Assert.False(string.IsNullOrWhiteSpace(check.Id)));
        Assert.Equal(
            report.Checks.Select(static check => check.Id).Distinct(StringComparer.Ordinal).Count(),
            report.Checks.Count);
    }

    /// <summary>
    /// The worst surface has to be in the set that gets measured. Measuring the resting card and
    /// not the hovered one is the specific mistake that shipped a tone at 3.52:1 once already.
    /// </summary>
    [Fact]
    public void The_hovered_card_is_among_the_measured_surfaces()
    {
        var palette = SchemeDerivation.Derive(new WinoraColorScheme
        {
            Canvas = ColorValue.Parse("#0C0C0F"),
            Accent = ColorValue.Parse("#A78BFA"),
        });

        var report = SchemeContrast.Measure(palette);

        Assert.Contains(report.Checks, check => check.Surface == palette.CardHover);
    }

    private static string Explain(SchemeContrastReport report) =>
        string.Join(
            "\n  ",
            report.Checks.Select(static check =>
                $"{check.Id}: {check.Foreground.ToHex()} on {check.Surface.ToHex()} — " +
                $"{check.Ratio:F2}:1 against {check.Floor:F1}:1 {(check.Passes ? "ok" : "FAILS")}"));
}
