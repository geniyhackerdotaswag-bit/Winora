namespace Winora.Core.Profile;

/// <param name="Score">How many of the four requirements are met, 0 to 4.</param>
public sealed record PasswordStrength(
    int Score,
    bool HasMinLength,
    bool HasNumber,
    bool HasUppercase,
    bool HasSpecial)
{
    /// <summary>
    /// Whether the registration screen will accept it.
    /// </summary>
    /// <remarks>
    /// Length is required outright; beyond that, two of the four is the bar. Demanding all four
    /// pushes people towards writing the password down, which is worse than a merely decent one.
    /// </remarks>
    public bool IsAcceptable => Score >= 2 && HasMinLength;
}

/// <summary>The four requirements shown as a checklist while somebody types.</summary>
public static class PasswordStrengthRules
{
    public const int MinLength = 8;

    public static PasswordStrength Evaluate(string? password)
    {
        var value = password ?? string.Empty;

        var hasMinLength = value.Length >= MinLength;
        var hasNumber = value.Any(char.IsDigit);
        var hasUppercase = value.Any(char.IsUpper);

        // Anything that is neither a letter nor a digit — punctuation, a symbol, a space. Written
        // as "not letter, not digit" rather than as a list of allowed symbols so that it holds for
        // every alphabet, including the Cyrillic these passwords are mostly typed in.
        var hasSpecial = value.Any(static character =>
            !char.IsLetterOrDigit(character));

        var score = 0;
        if (hasMinLength) { score++; }
        if (hasNumber) { score++; }
        if (hasUppercase) { score++; }
        if (hasSpecial) { score++; }

        return new PasswordStrength(score, hasMinLength, hasNumber, hasUppercase, hasSpecial);
    }
}
