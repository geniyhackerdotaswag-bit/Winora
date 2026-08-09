using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// The role order inside a scheme string is undocumented and was measured from a scheme that was
/// both applied and registered on a real machine. It is pinned here because getting it wrong is
/// silent: the cursors would all change, just to the wrong shapes, and the two diagonal resize
/// arrows would be swapped on every pack without anything reporting a failure.
/// </summary>
public sealed class CursorSchemeStoreTests
{
    /// <summary>
    /// Taken verbatim from an applied scheme, whose named values under Control Panel\Cursors were
    /// compared position by position to produce the expectations below.
    /// </summary>
    private const string MeasuredScheme =
        @"C:\C\normal.ani,C:\C\help.ani,C:\C\working.ani,C:\C\busy.ani,C:\C\precision.ani," +
        @"C:\C\text.ani,C:\C\hand.ani,C:\C\unavailable.ani,C:\C\vertical.ani,C:\C\horizontal.ani," +
        @"C:\C\diagonal1.ani,C:\C\diagonal2.ani,C:\C\move.ani,C:\C\alternate.ani,C:\C\link.ani,,";

    [Theory]
    [InlineData(CursorRole.Arrow, "normal.ani")]
    [InlineData(CursorRole.Help, "help.ani")]
    [InlineData(CursorRole.AppStarting, "working.ani")]
    [InlineData(CursorRole.Wait, "busy.ani")]
    [InlineData(CursorRole.Crosshair, "precision.ani")]
    [InlineData(CursorRole.IBeam, "text.ani")]
    [InlineData(CursorRole.NWPen, "hand.ani")]
    [InlineData(CursorRole.No, "unavailable.ani")]
    [InlineData(CursorRole.SizeNS, "vertical.ani")]
    [InlineData(CursorRole.SizeWE, "horizontal.ani")]
    [InlineData(CursorRole.SizeAll, "move.ani")]
    [InlineData(CursorRole.UpArrow, "alternate.ani")]
    [InlineData(CursorRole.Hand, "link.ani")]
    public void Each_position_maps_to_the_role_it_was_measured_against(CursorRole role, string expected)
    {
        var files = WindowsCursorSchemeStore.ParseSchemeValue(MeasuredScheme);

        Assert.EndsWith(expected, files[role], StringComparison.Ordinal);
    }

    /// <summary>
    /// The pair most likely to be got wrong. The shipped file names suggest the opposite order, and
    /// a swap here is invisible until a user notices their resize arrows lean the wrong way.
    /// </summary>
    [Fact]
    public void The_two_diagonal_resize_roles_are_not_swapped()
    {
        var files = WindowsCursorSchemeStore.ParseSchemeValue(MeasuredScheme);

        Assert.EndsWith("diagonal1.ani", files[CursorRole.SizeNWSE], StringComparison.Ordinal);
        Assert.EndsWith("diagonal2.ani", files[CursorRole.SizeNESW], StringComparison.Ordinal);
    }

    /// <summary>Windows ships schemes with no Pin or Person cursor; blanks are absence, not error.</summary>
    [Fact]
    public void A_blank_position_leaves_the_role_absent()
    {
        var files = WindowsCursorSchemeStore.ParseSchemeValue(MeasuredScheme);

        Assert.False(files.ContainsKey(CursorRole.Pin));
        Assert.False(files.ContainsKey(CursorRole.Person));
    }

    [Fact]
    public void A_short_scheme_is_read_as_far_as_it_goes()
    {
        var files = WindowsCursorSchemeStore.ParseSchemeValue(@"C:\C\a.cur,C:\C\b.cur");

        Assert.Equal(2, files.Count);
        Assert.EndsWith("a.cur", files[CursorRole.Arrow], StringComparison.Ordinal);
        Assert.EndsWith("b.cur", files[CursorRole.Help], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void A_scheme_with_nothing_in_it_yields_no_roles(string value)
    {
        Assert.Empty(WindowsCursorSchemeStore.ParseSchemeValue(value));
    }

    /// <summary>
    /// A longer list than Winora knows roles for must not throw. Windows could add a role, and an
    /// exception here would take the whole screen down over a cursor nobody asked about.
    /// </summary>
    [Fact]
    public void Extra_positions_beyond_the_known_roles_are_ignored()
    {
        var value = string.Join(',', Enumerable.Repeat(@"C:\C\x.cur", 25));

        var files = WindowsCursorSchemeStore.ParseSchemeValue(value);

        Assert.Equal(Enum.GetValues<CursorRole>().Length, files.Count);
    }

    /// <summary>Reading the live machine must never throw, whatever it happens to hold.</summary>
    [Fact]
    public void Reading_the_installed_schemes_is_safe()
    {
        var schemes = new WindowsCursorSchemeStore().Schemes();

        Assert.All(schemes, scheme =>
        {
            Assert.False(string.IsNullOrWhiteSpace(scheme.Name));
            Assert.NotEmpty(scheme.Files);
        });
    }
}
