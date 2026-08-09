using Winora.Core.Appearance;
using Xunit;

namespace Winora.Core.Tests.Appearance;

/// <summary>
/// A colour that survives the trip through <c>app-settings.json</c> and back.
/// </summary>
/// <remarks>
/// The scheme is persisted as text the user can open and edit by hand, so parsing has to refuse
/// nonsense rather than coerce it. A silently coerced colour is the shape of defect that produces
/// an app nobody can read and no error anywhere.
/// </remarks>
public sealed class ColorValueTests
{
    [Theory]
    [InlineData("#0C0C0F", 0x0C, 0x0C, 0x0F)]
    [InlineData("0C0C0F", 0x0C, 0x0C, 0x0F)]
    [InlineData("#a78bfa", 0xA7, 0x8B, 0xFA)]
    [InlineData("#FFF", 0xFF, 0xFF, 0xFF)]
    [InlineData("#08a", 0x00, 0x88, 0xAA)]
    public void A_valid_hex_string_parses(string text, byte r, byte g, byte b) =>
        Assert.Equal(new ColorValue(r, g, b), ColorValue.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("#0C0C0F0C")]
    public void Anything_else_is_refused(string? text)
    {
        Assert.False(ColorValue.TryParse(text, out _));
        Assert.Throws<FormatException>(() => ColorValue.Parse(text!));
    }

    [Fact]
    public void The_hex_form_round_trips_in_upper_case()
    {
        var colour = ColorValue.Parse("#a78bfa");

        Assert.Equal("#A78BFA", colour.ToHex());
        Assert.Equal(colour, ColorValue.Parse(colour.ToHex()));
    }
}
