using Winora.Core.Appearance;
using Xunit;

namespace Winora.Core.Tests.Appearance;

/// <summary>
/// The arithmetic every other appearance decision rests on.
/// </summary>
/// <remarks>
/// These numbers are fixed by WCAG 2.1, not chosen by Winora, so they are asserted against the
/// published constants rather than against whatever the implementation happens to produce. The
/// palette test suite in <c>Winora.Architecture.Tests</c> carried its own private copy of this
/// arithmetic; now that a user can compose a scheme at runtime the same maths has to be reachable
/// from the app, and one implementation measured by both is the point.
/// </remarks>
public sealed class ContrastMathTests
{
    [Fact]
    public void Black_on_white_is_the_maximum_ratio()
    {
        var ratio = ContrastMath.Ratio(
            new ColorValue(0, 0, 0),
            new ColorValue(255, 255, 255));

        Assert.Equal(21.0, ratio, 3);
    }

    [Fact]
    public void A_colour_against_itself_is_one_to_one()
    {
        var colour = new ColorValue(0x6D, 0x3F, 0xD4);
        Assert.Equal(1.0, ContrastMath.Ratio(colour, colour), 6);
    }

    /// <summary>Order must not matter: the brighter side always becomes the numerator.</summary>
    [Fact]
    public void The_ratio_is_symmetric()
    {
        var a = new ColorValue(0x0C, 0x0C, 0x0F);
        var b = new ColorValue(0xAF, 0xB3, 0xBA);

        Assert.Equal(ContrastMath.Ratio(a, b), ContrastMath.Ratio(b, a), 9);
    }

    [Theory]
    [InlineData(0, 0, 0, 0.0)]
    [InlineData(255, 255, 255, 1.0)]
    public void Relative_luminance_spans_zero_to_one(byte r, byte g, byte b, double expected) =>
        Assert.Equal(expected, ContrastMath.RelativeLuminance(new ColorValue(r, g, b)), 6);

    /// <summary>
    /// Green carries most of the luminance and blue almost none. Getting these coefficients the
    /// wrong way round yields a function that still looks plausible and passes a naive test.
    /// </summary>
    [Fact]
    public void Green_weighs_more_than_red_which_weighs_more_than_blue()
    {
        var green = ContrastMath.RelativeLuminance(new ColorValue(0, 255, 0));
        var red = ContrastMath.RelativeLuminance(new ColorValue(255, 0, 0));
        var blue = ContrastMath.RelativeLuminance(new ColorValue(0, 0, 255));

        Assert.True(green > red && red > blue, $"green {green}, red {red}, blue {blue}");
    }

    [Fact]
    public void Blending_none_of_the_second_colour_returns_the_first()
    {
        var under = new ColorValue(0x0C, 0x0C, 0x0F);
        Assert.Equal(under, ContrastMath.Blend(under, new ColorValue(255, 255, 255), 0.0));
    }

    [Fact]
    public void Blending_all_of_the_second_colour_returns_the_second()
    {
        var over = new ColorValue(255, 252, 252);
        Assert.Equal(over, ContrastMath.Blend(new ColorValue(0x0C, 0x0C, 0x0F), over, 1.0));
    }

    /// <summary>
    /// Compositing a translucent layer is the same operation as blending toward a colour, which is
    /// why there is one function rather than two that could drift apart.
    /// </summary>
    [Fact]
    public void Blending_halfway_lands_between_the_two()
    {
        var result = ContrastMath.Blend(
            new ColorValue(0, 0, 0),
            new ColorValue(200, 100, 50),
            0.5);

        Assert.Equal(new ColorValue(100, 50, 25), result);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void A_blend_amount_outside_the_unit_range_is_rejected(double amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ContrastMath.Blend(
            new ColorValue(0, 0, 0),
            new ColorValue(255, 255, 255),
            amount));
}
