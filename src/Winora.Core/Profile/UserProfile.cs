namespace Winora.Core.Profile;

/// <param name="Name">What to call this person. Never empty once stored.</param>
/// <param name="Email">Optional, and it stays on this machine. May be empty.</param>
/// <param name="Avatar">Index into the palette, or -1 for "work it out from the name".</param>
/// <param name="CreatedUtc">When the introduction happened.</param>
/// <param name="Password">
/// The password digest. Null only in memory, while a registration is being filled in — a stored
/// profile always has one, because registration is the only way a profile comes to exist.
/// </param>
public sealed record UserProfile(
    string Name,
    string Email,
    int Avatar,
    DateTimeOffset CreatedUtc,
    PasswordDigest? Password = null);

/// <summary>What the welcome form accepts.</summary>
public static class ProfileRules
{
    /// <summary>Long enough for a real name, short enough to fit the card.</summary>
    public const int NameMaxLength = 32;

    /// <summary>The longest an address can be in practice.</summary>
    public const int EmailMaxLength = 254;

    /// <summary>Trimmed, because surrounding space is typing rather than a name.</summary>
    public static string NormaliseName(string? name) => name?.Trim() ?? string.Empty;

    /// <summary>Whether this looks like a real name.</summary>
    /// <remarks>Accepts 1–32 characters after trimming. Surrounding space is typing, not the name.</remarks>
    public static bool IsNameValid(string? name)
    {
        var trimmed = NormaliseName(name);
        return trimmed.Length is > 0 and <= NameMaxLength;
    }

    /// <summary>
    /// Whether this looks like an email address.
    /// </summary>
    /// <remarks>
    /// Shape only: something before the single @, something after it, and a dot inside a domain
    /// that does not start or end with one. Whether the mailbox exists cannot be established
    /// without sending to it, and this program has no server and sends nothing — a check that
    /// looked deeper would only be pretending. Empty passes: the field is optional.
    /// </remarks>
    public static bool IsEmailValid(string? email)
    {
        var trimmed = email?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > EmailMaxLength || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var parts = trimmed.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0)
        {
            return false;
        }

        var domain = parts[1];
        return domain.Length >= 3 &&
               domain.Contains('.') &&
               !domain.StartsWith('.') &&
               !domain.EndsWith('.') &&
               !domain.Contains("..");
    }
}
