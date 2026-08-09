using System.Globalization;
using System.Text.RegularExpressions;

namespace Winora.System.Windows;

/// <summary>
/// Turns a folder or archive name into something worth showing on a card.
/// </summary>
/// <remarks>
/// Packs downloaded from theme sites arrive as <c>arlecchino_dd433cfc04_VSTHEMES-ORG</c> or
/// <c>chroma_cur_black_m_v20180130</c>. Showing that verbatim makes the screen look like a file
/// listing. The cleanup is deliberately conservative: it removes only tokens that are demonstrably
/// noise — the download hash, the site stamp, a date-version, and the word "cursor" in a list of
/// cursors — and leaves everything else alone rather than trying to be clever about words it does
/// not recognise.
/// </remarks>
public static class CursorPackNaming
{
    /// <summary>A download id: a long run of hex that no human chose.</summary>
    private static readonly Regex HashToken = new("^[0-9a-f]{8,}$", RegexOptions.Compiled);

    /// <summary>A date-stamped version such as <c>v20180130</c>.</summary>
    private static readonly Regex DateVersionToken = new(@"^v\d{6,8}$", RegexOptions.Compiled);

    /// <summary>
    /// The stamp theme sites append, such as <c>_VSTHEMES-ORG</c>. Stripped from the whole name
    /// before it is split, because splitting on the hyphen first would break it into two tokens and
    /// leave "ORG" behind — which is exactly what a test caught.
    /// </summary>
    private static readonly Regex SiteStamp = new(
        @"[_-][A-Za-z0-9]+-(ORG|COM|NET|RU)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] NoiseWords = ["cur", "curs", "cursor", "cursors", "set", "pack"];

    /// <summary>
    /// A folder name that says nothing about the pack: "123", "вариант1", "New folder".
    /// </summary>
    private static readonly Regex Uninformative = new(
        @"^(\d+|(вариант|variant|новая папка|new folder)\s*\d*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsUninformative(string folderName) =>
        string.IsNullOrWhiteSpace(folderName) || Uninformative.IsMatch(folderName.Trim());

    /// <summary>
    /// The prefix every cursor file in a pack shares, if there is one worth using.
    /// </summary>
    /// <remarks>
    /// Packs are routinely named through their files rather than their folder: a folder called
    /// "321" holding "neon arrow.ani" and "neon busy.ani" is the Neon pack. Deriving the name from
    /// what is actually there beats inventing one, because the user can still see where it came
    /// from.
    /// </remarks>
    public static string CommonPrefixOf(IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count < 2)
        {
            return string.Empty;
        }

        var first = Path.GetFileNameWithoutExtension(fileNames[0]);
        var length = first.Length;

        foreach (var candidate in fileNames.Skip(1))
        {
            var other = Path.GetFileNameWithoutExtension(candidate);
            var shared = 0;
            while (shared < length &&
                   shared < other.Length &&
                   char.ToLowerInvariant(first[shared]) == char.ToLowerInvariant(other[shared]))
            {
                shared++;
            }

            length = shared;
            if (length == 0)
            {
                return string.Empty;
            }
        }

        // Trimmed to a word boundary so "neon " does not become "neon a" from an unlucky pair, and
        // required to be long enough that a one or two letter accident is not treated as a name.
        var prefix = first[..length].Trim(' ', '_', '-');
        return prefix.Length >= 3 ? prefix : string.Empty;
    }

    public static string Clean(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        var tokens = SiteStamp.Replace(rawName, string.Empty)
            .Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => !HashToken.IsMatch(token))
            .Where(static token => !DateVersionToken.IsMatch(token))
            .Where(static token => !NoiseWords.Contains(token, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (tokens.Length == 0)
        {
            // Everything looked like noise, which means the guess was wrong. Better the raw name
            // than an empty card.
            return rawName;
        }

        var culture = CultureInfo.CurrentCulture;
        return string.Join(' ', tokens.Select(token => Capitalize(token, culture)));
    }

    /// <summary>
    /// Combines the pack name with a set name, for archives that hold several sets at once.
    /// </summary>
    public static string Combine(string packName, string setName) =>
        string.IsNullOrWhiteSpace(setName)
            ? packName
            : $"{packName} · {Capitalize(setName.Trim(), CultureInfo.CurrentCulture)}";

    private static string Capitalize(string token, CultureInfo culture)
    {
        // An all-caps token is left alone: it is an acronym or a set tag such as IR or TB, and
        // title-casing it to "Ir" reads as a typo.
        if (token.Length > 1 && token.All(static c => !char.IsLower(c)))
        {
            return token;
        }

        return char.ToUpper(token[0], culture) + token[1..].ToLower(culture);
    }
}
