namespace Winora.Core.Licence;

/// <summary>
/// What a Winora key looks like, and how to read one somebody typed.
/// </summary>
/// <remarks>
/// <para>
/// The same rules as the site's <c>Licences</c> class, restated here so a mistyped key is refused
/// on this machine instead of costing a round trip and a vague answer. The two must agree: a key
/// this refuses is a key the site would never have issued.
/// </para>
/// <para>
/// The alphabet carries no zero and no letter O, no one and no letter I. A key is read off a
/// screen or out of a message and typed by hand, and "O" for "0" would be a support request on
/// every hundredth sale.
/// </para>
/// </remarks>
public static class LicenceKey
{
    /// <summary>Letters a key can contain. No 0/O, no 1/I.</summary>
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>How many letters, not counting the prefix and the dashes.</summary>
    public const int Length = 16;

    /// <summary>The prefix that makes a key recognisable in a message.</summary>
    public const string Prefix = "WNR";

    /// <summary>
    /// Strips a typed key down to what is compared.
    /// </summary>
    /// <remarks>
    /// People type a key with dashes and without, upper and lower case, with the prefix and
    /// without, and with a space stuck to the end from the copy. Comparing it as typed means
    /// refusing a correct key and telling the person they are wrong when we are.
    /// </remarks>
    public static string Normalize(string? typed)
    {
        if (string.IsNullOrEmpty(typed))
        {
            return string.Empty;
        }

        var clean = new string([.. typed
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)]);

        return clean.StartsWith(Prefix, StringComparison.Ordinal)
            ? clean[Prefix.Length..]
            : clean;
    }

    /// <summary>Whether this could be a key at all — asked before any request is sent.</summary>
    public static bool IsWellFormed(string? typed)
    {
        var body = Normalize(typed);

        return body.Length == Length && body.All(static letter => Alphabet.Contains(letter));
    }

    /// <summary>A key as it is shown and printed: WNR-XXXX-XXXX-XXXX-XXXX.</summary>
    /// <remarks>
    /// Returns anything that is not a key body untouched. Showing a mangled version of what
    /// somebody typed is worse than showing it as they typed it.
    /// </remarks>
    public static string Format(string? typed)
    {
        var body = Normalize(typed);

        if (body.Length != Length)
        {
            return typed ?? string.Empty;
        }

        return Prefix + "-" + string.Join(
            '-',
            Enumerable.Range(0, Length / 4).Select(part => body.Substring(part * 4, 4)));
    }

    /// <summary>A key with everything but its last four letters hidden.</summary>
    /// <remarks>
    /// What the subscription screen shows once a key is stored. The whole key is not shown again
    /// on purpose: it is the only credential there is, and a screenshot of this window should not
    /// hand it to anybody.
    /// </remarks>
    public static string Mask(string? typed)
    {
        var body = Normalize(typed);

        return body.Length == Length
            ? $"{Prefix}-****-****-****-{body[^4..]}"
            : string.Empty;
    }
}
