using System.Globalization;

namespace Winora.Core.Appearance;

/// <summary>
/// One opaque colour, as three channels.
/// </summary>
/// <remarks>
/// Deliberately not <c>Windows.UI.Color</c>: <c>Winora.Core</c> stays platform-independent, and the
/// contrast arithmetic that decides whether a scheme may be applied has to be testable without
/// activating WinUI. The App layer converts at its own boundary.
/// </remarks>
public readonly record struct ColorValue(byte R, byte G, byte B)
{
    /// <summary>
    /// Parses <c>#RRGGBB</c>, <c>#RGB</c>, or either without the leading hash.
    /// </summary>
    /// <exception cref="FormatException">The text is not one of those forms.</exception>
    public static ColorValue Parse(string text) =>
        TryParse(text, out var value)
            ? value
            : throw new FormatException(
                $"'{text}' is not a colour. Expected #RRGGBB or #RGB.");

    /// <summary>
    /// Parses without throwing.
    /// </summary>
    /// <remarks>
    /// The scheme is persisted as text a person can open and edit, so a malformed value is an
    /// ordinary condition rather than an exceptional one. Nothing here coerces a near-miss into a
    /// colour: a silently accepted wrong value is how an app becomes unreadable with no error.
    /// </remarks>
    public static bool TryParse(string? text, out ColorValue value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = text.Trim().AsSpan();
        if (digits.Length > 0 && digits[0] == '#')
        {
            digits = digits[1..];
        }

        if (digits.Length is not (3 or 6))
        {
            return false;
        }

        foreach (var character in digits)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        value = digits.Length == 3
            ? new ColorValue(Nibble(digits[0]), Nibble(digits[1]), Nibble(digits[2]))
            : new ColorValue(Octet(digits[..2]), Octet(digits.Slice(2, 2)), Octet(digits.Slice(4, 2)));

        return true;
    }

    /// <summary>The canonical persisted form: uppercase, always six digits, always hashed.</summary>
    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    /// <summary>A shorthand digit means both nibbles, so <c>#08a</c> is <c>#0088AA</c>.</summary>
    private static byte Nibble(char digit)
    {
        var value = (byte)Convert.ToInt32(digit.ToString(), 16);
        return (byte)((value << 4) | value);
    }

    private static byte Octet(ReadOnlySpan<char> pair) =>
        byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
