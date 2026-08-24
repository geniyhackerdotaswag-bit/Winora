using Winora.Core.Profile;
using Xunit;

namespace Winora.Core.Tests.Profile;

/// <summary>
/// The four requirements the registration screen ticks off as somebody types.
/// </summary>
/// <remarks>
/// Copied in substance from the reference the owner supplied: at least eight characters, a digit,
/// a capital, and something that is neither. The capital and the "something else" both have to
/// understand Cyrillic, because the people typing here are typing Russian.
/// </remarks>
public sealed class PasswordStrengthTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    [InlineData("abcdefgh", 1)]
    [InlineData("abcdefg1", 2)]
    [InlineData("Abcdefg1", 3)]
    [InlineData("Abcdefg1!", 4)]
    public void The_score_counts_the_requirements_met(string? password, int expected)
    {
        Assert.Equal(expected, PasswordStrengthRules.Evaluate(password).Score);
    }

    [Fact]
    public void Each_requirement_is_reported_on_its_own()
    {
        var strength = PasswordStrengthRules.Evaluate("Abcdefg1!");

        Assert.True(strength.HasMinLength);
        Assert.True(strength.HasNumber);
        Assert.True(strength.HasUppercase);
        Assert.True(strength.HasSpecial);
    }

    [Fact]
    public void A_short_password_fails_only_the_length()
    {
        var strength = PasswordStrengthRules.Evaluate("Ab1!");

        Assert.False(strength.HasMinLength);
        Assert.True(strength.HasNumber);
        Assert.True(strength.HasUppercase);
        Assert.True(strength.HasSpecial);
    }

    /// <summary>The people typing here type Russian, so Cyrillic has to count.</summary>
    [Fact]
    public void A_cyrillic_capital_counts_as_a_capital()
    {
        Assert.True(PasswordStrengthRules.Evaluate("Пароль12").HasUppercase);
    }

    [Fact]
    public void A_cyrillic_letter_is_not_a_special_character()
    {
        Assert.False(PasswordStrengthRules.Evaluate("Пароль12").HasSpecial);
    }

    [Theory]
    [InlineData("Пароль1!")]
    [InlineData("пароль 1")]
    public void A_space_or_a_symbol_counts_as_special(string password)
    {
        Assert.True(PasswordStrengthRules.Evaluate(password).HasSpecial);
    }

    /// <summary>
    /// What the "Готово" button actually waits for: eight characters and at least two requirements.
    /// </summary>
    [Theory]
    [InlineData("abcdefgh", false)]
    [InlineData("abcdefg1", true)]
    [InlineData("Ab1!", false)]
    [InlineData("Abcdefg1!", true)]
    public void Acceptable_means_long_enough_and_not_trivial(string password, bool expected)
    {
        Assert.Equal(expected, PasswordStrengthRules.Evaluate(password).IsAcceptable);
    }

    /// <summary>
    /// Nothing but spaces is not a password, however many of them there are.
    /// </summary>
    /// <remarks>
    /// It used to score two — the length rule counted the spaces, and the special rule counted a
    /// space as "not a letter, not a digit" — which lit two ticks on the checklist and turned the
    /// button on. A nudge that shows green for an empty string is worse than no nudge.
    /// </remarks>
    [Theory]
    [InlineData("        ")]
    [InlineData("               ")]
    [InlineData("\t\t\t\t\t\t\t\t")]
    public void Nothing_but_whitespace_is_not_long_enough(string password)
    {
        var strength = PasswordStrengthRules.Evaluate(password);

        Assert.False(strength.HasMinLength);
        Assert.False(strength.IsAcceptable);
    }

    /// <summary>Spaces between words are content, and still count towards the length.</summary>
    [Fact]
    public void A_passphrase_with_spaces_is_still_long_enough()
    {
        var strength = PasswordStrengthRules.Evaluate("два слова");

        Assert.True(strength.HasMinLength);
        Assert.True(strength.HasSpecial);
    }
}
