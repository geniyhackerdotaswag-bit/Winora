using Winora.Core.Profile;
using Xunit;

namespace Winora.Core.Tests.Profile;

/// <summary>
/// The drawn mark: one letter and a colour.
/// </summary>
/// <remarks>
/// Drawn rather than shipped as pictures — nothing to weigh, nobody else's artwork to license, and
/// no blur at any size. The colour has to be stable: a person whose mark changed colour between
/// launches would reasonably wonder what else the program forgets.
/// </remarks>
public sealed class ProfileAvatarTests
{
    [Fact]
    public void The_same_name_always_gets_the_same_colour()
    {
        var first = ProfileAvatar.ColourFor("Аня", ProfileAvatar.FromName);
        var second = ProfileAvatar.ColourFor("Аня", ProfileAvatar.FromName);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_chosen_colour_wins_over_the_derived_one()
    {
        Assert.Equal(ProfileAvatar.Palette[2], ProfileAvatar.ColourFor("Аня", 2));
    }

    [Fact]
    public void The_derived_colour_is_always_one_from_the_palette()
    {
        foreach (var name in new[] { "Аня", "Bob", "x", "Пользователь Windows", "12345" })
        {
            Assert.Contains(ProfileAvatar.ColourFor(name, ProfileAvatar.FromName), ProfileAvatar.Palette);
        }
    }

    /// <summary>An index from a future version, or a corrupt file, must not crash the card.</summary>
    [Theory]
    [InlineData(-5)]
    [InlineData(99)]
    public void An_index_outside_the_palette_falls_back_to_the_name(int avatar)
    {
        Assert.Equal(
            ProfileAvatar.ColourFor("Аня", ProfileAvatar.FromName),
            ProfileAvatar.ColourFor("Аня", avatar));
    }

    [Theory]
    [InlineData("Аня", "А")]
    [InlineData("bob", "B")]
    [InlineData("  пётр  ", "П")]
    public void The_initial_is_the_first_letter_in_capitals(string name, string expected)
    {
        Assert.Equal(expected, ProfileAvatar.InitialFor(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_name_there_is_still_something_to_draw(string? name)
    {
        Assert.False(string.IsNullOrEmpty(ProfileAvatar.InitialFor(name)));
        Assert.Contains(ProfileAvatar.ColourFor(name, ProfileAvatar.FromName), ProfileAvatar.Palette);
    }
}
