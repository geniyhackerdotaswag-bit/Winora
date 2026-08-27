using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Windows;

/// <summary>
/// Ordering names the way somebody reads them.
/// </summary>
/// <remarks>
/// The bypass list is thirteen strategies numbered ALT to ALT13, and plain text comparison put
/// ALT10 second. Correct for text; wrong for a list a person works down in order, trying the next
/// one when the last did not help.
/// </remarks>
public sealed class NaturalOrderTests
{
    private static string[] Sorted(params string[] names) =>
        [.. names.OrderBy(static name => name, NaturalOrder.Instance)];

    [Fact]
    public void The_bypass_strategies_come_out_in_the_order_they_are_numbered()
    {
        var names = Enumerable.Range(2, 12)
            .Select(static number => $"general (ALT{number})")
            .Append("general (ALT)")
            .ToArray();

        var sorted = Sorted(names);

        Assert.Equal("general (ALT)", sorted[0]);
        Assert.Equal("general (ALT2)", sorted[1]);
        Assert.Equal("general (ALT10)", sorted[9]);
        Assert.Equal("general (ALT13)", sorted[^1]);
    }

    [Fact]
    public void Ten_comes_after_two()
    {
        Assert.Equal(["a2", "a10"], Sorted("a10", "a2"));
    }

    /// <summary>A name without digits sorts before the same name with them.</summary>
    [Fact]
    public void A_bare_name_comes_before_its_numbered_versions()
    {
        Assert.Equal(["alt", "alt1", "alt2"], Sorted("alt2", "alt", "alt1"));
    }

    /// <summary>Leading zeros are not a different number.</summary>
    [Fact]
    public void Leading_zeros_do_not_change_the_order()
    {
        Assert.Equal(["x02", "x3"], Sorted("x3", "x02"));
    }

    [Fact]
    public void Numbers_in_more_than_one_place_are_each_read_as_numbers()
    {
        Assert.Equal(["v1 part2", "v1 part10", "v2 part1"], Sorted("v2 part1", "v1 part10", "v1 part2"));
    }

    [Fact]
    public void Case_does_not_decide_the_order()
    {
        Assert.Equal(["ALT2", "alt10"], Sorted("alt10", "ALT2"));
    }

    /// <summary>
    /// A run of digits too long to be a number is still ordered, not thrown at the caller.
    /// </summary>
    /// <remarks>
    /// Nothing in a strategy name looks like this, but the comparer is handed file names, and a
    /// comparer that throws takes the whole screen with it.
    /// </remarks>
    [Fact]
    public void An_absurdly_long_number_is_ordered_rather_than_thrown()
    {
        var huge = new string('9', 40);

        var sorted = Sorted("a" + huge, "a1");

        Assert.Equal(2, sorted.Length);
    }

    [Fact]
    public void Nothing_sorts_before_something()
    {
        Assert.True(NaturalOrder.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalOrder.Instance.Compare("a", null) > 0);
        Assert.Equal(0, NaturalOrder.Instance.Compare(null, null));
    }

    [Fact]
    public void Equal_names_are_equal()
    {
        Assert.Equal(0, NaturalOrder.Instance.Compare("general (ALT7)", "general (ALT7)"));
    }
}
