using System.Globalization;

namespace Winora.Core.Profile;

/// <summary>The drawn mark: one letter on a coloured circle.</summary>
/// <remarks>
/// Drawn rather than shipped as images: nothing to weigh, no third-party artwork to account for,
/// and no blur at any size. The card needs a mark at 32 px and at 96 px from the same source.
/// </remarks>
public static class ProfileAvatar
{
    /// <summary>Stored in place of a chosen colour, meaning "work it out from the name".</summary>
    public const int FromName = -1;

    /// <summary>Shown when there is no name at all to take a letter from.</summary>
    private const string FallbackInitial = "?";

    public static IReadOnlyList<string> Palette { get; } =
    [
        "#7C6BF5",
        "#3FA9F5",
        "#2FBF9E",
        "#E0913A",
        "#D9536F",
        "#8E7CC3",
    ];

    /// <summary>The colour for this person: chosen if they chose one, derived otherwise.</summary>
    /// <remarks>
    /// An index the palette does not contain — from a corrupt file, or from a future version with
    /// more colours — falls back to the derived colour rather than throwing. A card is decoration,
    /// and decoration must not be able to stop a screen from drawing.
    /// </remarks>
    public static string ColourFor(string? name, int avatar)
    {
        if (avatar >= 0 && avatar < Palette.Count)
        {
            return Palette[avatar];
        }

        return Palette[Bucket(ProfileRules.NormaliseName(name))];
    }

    public static string InitialFor(string? name)
    {
        var trimmed = ProfileRules.NormaliseName(name);

        if (trimmed.Length == 0)
        {
            return FallbackInitial;
        }

        // Get the first grapheme as a whole unit, not just one UTF-16 code unit.
        // This handles supplementary-plane characters (emoji, rare scripts) and combining diacritics correctly.
        var firstGrapheme = StringInfo.GetNextTextElement(trimmed, 0);

        // If the grapheme is more than one character, it is either a surrogate pair
        // or a character with combining marks. Upcase the whole thing as a string.
        if (firstGrapheme.Length > 1)
        {
            return char.ToUpper(firstGrapheme[0], CultureInfo.InvariantCulture) + firstGrapheme.Substring(1);
        }

        // Use InvariantCulture to ensure the mark is stable across locales.
        // On tr-TR or az-AZ machines, CurrentCulture would upcase 'i' to 'İ' rather than 'I',
        // making the mark depend on where the machine is — the same instability Bucket's comment warns about.
        return char.ToUpper(firstGrapheme[0], CultureInfo.InvariantCulture).ToString();
    }

    /// <summary>
    /// Which colour a name lands on.
    /// </summary>
    /// <remarks>
    /// A plain sum of UTF-16 code units, not a cryptographic hash and not <c>string.GetHashCode</c>. The
    /// second is randomised per process in .NET Core, so the same person would get a different
    /// colour on every launch — which is exactly the thing this must not do.
    /// </remarks>
    private static int Bucket(string name)
    {
        var total = 0;
        foreach (var character in name)
        {
            total = (total + character) % Palette.Count;
        }

        return total;
    }
}
