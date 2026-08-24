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

        return trimmed.Length == 0
            ? FallbackInitial
            : char.ToUpper(trimmed[0], CultureInfo.CurrentCulture).ToString();
    }

    /// <summary>
    /// Which colour a name lands on.
    /// </summary>
    /// <remarks>
    /// A plain sum of code points, not a cryptographic hash and not <c>string.GetHashCode</c>. The
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
