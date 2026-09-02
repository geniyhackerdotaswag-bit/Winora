using Winora.Core.Licence;
using Xunit;

namespace Winora.Core.Tests.Licence;

/// <summary>
/// Reading a key somebody typed.
/// </summary>
/// <remarks>
/// These rules must agree with the site's <c>Licences</c> class. A key this refuses is a key the
/// site would never have issued; a key this mangles is a paid subscription that will not start.
/// </remarks>
public sealed class LicenceKeyTests
{
    private const string Body = "23456789ABCDEFGH";
    private const string Written = "WNR-2345-6789-ABCD-EFGH";

    /// <summary>People type a key every way there is, and every way must work.</summary>
    [Theory]
    [InlineData("WNR-2345-6789-ABCD-EFGH")]
    [InlineData("wnr-2345-6789-abcd-efgh")]
    [InlineData("WNR2345 6789 ABCD EFGH")]
    [InlineData("  WNR-2345-6789-ABCD-EFGH  ")]
    [InlineData("2345-6789-ABCD-EFGH")]
    [InlineData("23456789ABCDEFGH")]
    public void The_same_key_written_any_way_reads_the_same(string typed)
    {
        Assert.Equal(Body, LicenceKey.Normalize(typed));
        Assert.True(LicenceKey.IsWellFormed(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("WNR-2345")]
    [InlineData("23456789ABCDEFGHJ")]
    public void Anything_of_the_wrong_length_is_not_a_key(string? typed)
    {
        Assert.False(LicenceKey.IsWellFormed(typed));
    }

    /// <summary>
    /// The letters people confuse are not in a key, so a key containing them is not ours.
    /// </summary>
    /// <remarks>
    /// Zero and O, one and I. Somebody reading a key off a screen types the wrong one of each pair
    /// eventually, and catching it here turns a support request into a sentence on screen.
    /// </remarks>
    [Theory]
    [InlineData("WNR-0000-0000-0000-0000")]
    [InlineData("WNR-IIII-IIII-IIII-IIII")]
    [InlineData("WNR-2345-6789-ABCD-EFG0")]
    [InlineData("WNR-2345-6789-ABCD-EFGO")]
    public void A_key_never_contains_the_letters_people_confuse(string typed)
    {
        Assert.False(LicenceKey.IsWellFormed(typed));
    }

    [Fact]
    public void A_key_is_shown_in_groups_of_four_with_the_prefix()
    {
        Assert.Equal(Written, LicenceKey.Format(Body));
        Assert.Equal(Written, LicenceKey.Format(Written));
        Assert.Equal(Written, LicenceKey.Format("wnr2345 6789abcdefgh"));
    }

    /// <summary>Something that is not a key comes back untouched, not mangled.</summary>
    [Fact]
    public void What_is_not_a_key_is_shown_as_it_was_typed()
    {
        Assert.Equal("не ключ", LicenceKey.Format("не ключ"));
        Assert.Equal(string.Empty, LicenceKey.Format(null));
    }

    /// <summary>
    /// Only the last four letters survive masking.
    /// </summary>
    /// <remarks>
    /// The key is the only credential there is. A screenshot of the subscription screen must not
    /// hand it to whoever sees the screenshot.
    /// </remarks>
    [Fact]
    public void A_stored_key_is_shown_with_everything_but_its_tail_hidden()
    {
        var masked = LicenceKey.Mask(Written);

        Assert.Equal("WNR-****-****-****-EFGH", masked);
        Assert.DoesNotContain("2345", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("6789", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCD", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_nothing_to_mask_when_there_is_no_key()
    {
        Assert.Equal(string.Empty, LicenceKey.Mask(null));
        Assert.Equal(string.Empty, LicenceKey.Mask("не ключ"));
    }
}
