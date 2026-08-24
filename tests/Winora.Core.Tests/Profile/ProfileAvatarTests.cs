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
    public void The_colour_derivation_is_pinned_for_stability()
    {
        // These values are pinned deliberately. If the derivation algorithm ever changes,
        // everybody's mark will change colour, and that is a decision, not an accident.
        Assert.Equal("#7C6BF5", ProfileAvatar.ColourFor("Аня", ProfileAvatar.FromName));
        Assert.Equal("#3FA9F5", ProfileAvatar.ColourFor("bob", ProfileAvatar.FromName));
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

    [Fact]
    public void A_name_starting_with_an_emoji_returns_a_valid_initial()
    {
        var initial = ProfileAvatar.InitialFor("🎨 Alice");

        // The initial must be non-empty.
        Assert.False(string.IsNullOrEmpty(initial));

        // It must not be a lone surrogate (which would be invalid UTF-16).
        // The emoji is a surrogate pair (two chars), so if it is a lone surrogate,
        // it means we only got the high surrogate, which is broken.
        Assert.False(initial.Length == 1 && char.IsHighSurrogate(initial[0]));
        Assert.False(initial.Length == 1 && char.IsLowSurrogate(initial[0]));
    }

    [Fact]
    public void A_name_in_decomposed_form_preserves_combining_marks()
    {
        // "É" composed (U+00C9) vs decomposed (U+0045 U+0301: 'E' + combining acute)
        var composed = "Érik";
        var decomposed = "E\u0301rik";  // Explicitly decomposed

        // Both should produce initials.
        var composedInitial = ProfileAvatar.InitialFor(composed);
        var decomposedInitial = ProfileAvatar.InitialFor(decomposed);

        // Both should be non-empty.
        Assert.False(string.IsNullOrEmpty(composedInitial));
        Assert.False(string.IsNullOrEmpty(decomposedInitial));

        // The decomposed form should have multiple characters (letter + combining mark).
        Assert.True(decomposedInitial.Length > 1);
    }
}
