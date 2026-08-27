using System.Globalization;

namespace Winora.System.Windows;

/// <summary>
/// Orders names the way a person reads them, so ALT2 comes before ALT10.
/// </summary>
/// <remarks>
/// Compared letter by letter until a digit turns up on both sides, at which point the whole run of
/// digits is read as one number. Plain text comparison puts "general (ALT10)" second in a list of
/// thirteen, because "1" sorts before "2" — which is correct for text and wrong for everything a
/// person expects from a numbered list.
/// </remarks>
public sealed class NaturalOrder : IComparer<string>
{
    public static NaturalOrder Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var l = 0;
        var r = 0;

        while (l < left.Length && r < right.Length)
        {
            if (char.IsDigit(left[l]) && char.IsDigit(right[r]))
            {
                var byNumber = CompareNumbers(left, ref l, right, ref r);

                if (byNumber != 0)
                {
                    return byNumber;
                }

                continue;
            }

            var byLetter = char.ToUpperInvariant(left[l]).CompareTo(char.ToUpperInvariant(right[r]));

            if (byLetter != 0)
            {
                return byLetter;
            }

            l++;
            r++;
        }

        return (left.Length - l).CompareTo(right.Length - r);
    }

    /// <summary>
    /// Compares the runs of digits starting at each position, and steps both past them.
    /// </summary>
    /// <remarks>
    /// Parsed rather than compared digit by digit, so leading zeros do not change the order:
    /// "ALT02" and "ALT2" are the same number and sort together. A run too long to be a number is
    /// compared as text, which is the only thing left to do with it and is stable.
    /// </remarks>
    private static int CompareNumbers(string left, ref int l, string right, ref int r)
    {
        var leftStart = l;
        var rightStart = r;

        while (l < left.Length && char.IsDigit(left[l]))
        {
            l++;
        }

        while (r < right.Length && char.IsDigit(right[r]))
        {
            r++;
        }

        var leftDigits = left[leftStart..l];
        var rightDigits = right[rightStart..r];

        return long.TryParse(leftDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber) &&
            long.TryParse(rightDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber)
                ? leftNumber.CompareTo(rightNumber)
                : string.CompareOrdinal(leftDigits, rightDigits);
    }
}
