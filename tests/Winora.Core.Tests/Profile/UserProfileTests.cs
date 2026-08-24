using Winora.Core.Profile;
using Xunit;

namespace Winora.Core.Tests.Profile;

/// <summary>
/// What the welcome form will and will not accept.
/// </summary>
/// <remarks>
/// The email rule is deliberately shallow: it checks the shape and nothing else. Whether a mailbox
/// exists cannot be established without a server, and this program has none — a check that looked
/// deeper would be pretending to know something it cannot.
/// </remarks>
public sealed class UserProfileTests
{
    [Theory]
    [InlineData("Аня")]
    [InlineData("a")]
    [InlineData("Пользователь Windows")]
    public void A_reasonable_name_is_accepted(string name)
    {
        Assert.True(ProfileRules.IsNameValid(name));
    }

    [Fact]
    public void A_name_of_exactly_the_limit_is_accepted()
    {
        Assert.True(ProfileRules.IsNameValid(new string('a', ProfileRules.NameMaxLength)));
    }

    [Fact]
    public void A_name_one_over_the_limit_is_not()
    {
        Assert.False(ProfileRules.IsNameValid(new string('a', ProfileRules.NameMaxLength + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Nothing_is_not_a_name(string? name)
    {
        Assert.False(ProfileRules.IsNameValid(name));
    }

    /// <summary>Surrounding space is the person's typing, not their name.</summary>
    [Fact]
    public void A_name_is_trimmed_before_it_is_judged_or_kept()
    {
        Assert.True(ProfileRules.IsNameValid("  Аня  "));
        Assert.Equal("Аня", ProfileRules.NormaliseName("  Аня  "));
        Assert.Equal(string.Empty, ProfileRules.NormaliseName(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a@b.ru")]
    [InlineData("very.long.name+tag@mail.example.com")]
    public void An_acceptable_email(string? email)
    {
        Assert.True(ProfileRules.IsEmailValid(email));
    }

    /// <summary>An absent email is allowed, in all the shapes absence takes.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void An_absent_email_is_allowed(string? email)
    {
        Assert.True(ProfileRules.IsEmailValid(email));
    }

    [Theory]
    [InlineData("a@")]
    [InlineData("@b.ru")]
    [InlineData("ab.ru")]
    [InlineData("a@b")]
    [InlineData("a b@c.ru")]
    [InlineData("a@@b.ru")]
    [InlineData("a@b..ru")]
    [InlineData("a@bcom")]
    [InlineData("a@.ru")]
    [InlineData("a@b.")]
    public void An_email_that_is_not_one(string email)
    {
        Assert.False(ProfileRules.IsEmailValid(email));
    }

    [Fact]
    public void An_email_over_the_length_limit_is_not_one()
    {
        var tooLongEmail = "a@" + new string('x', 253);
        Assert.False(ProfileRules.IsEmailValid(tooLongEmail));
    }
}
