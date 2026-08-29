using Winora.Core.Settings;
using Xunit;

namespace Winora.Core.Tests.Settings;

/// <summary>
/// What a settings file carried between machines is allowed to ask for.
/// </summary>
/// <remarks>
/// The file is text somebody moved by hand — a cloud folder, a flash drive, a message to oneself.
/// It is read as a proposal, never as instructions: an entry this build does not recognise is
/// reported to the person, not applied and not quietly dropped.
/// </remarks>
public sealed class SettingsBundleTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "winora.explorer.file-extensions",
        "winora.shell.taskbar-alignment",
        "windows.appearance.theme",
    };

    private static IReadOnlyList<SettingsCandidate> Examine(params (string Id, string Value)[] entries) =>
        SettingsBundle.Examine(entries.Select(e => new SettingsEntry(e.Id, e.Value)), Known);

    [Fact]
    public void A_setting_this_build_knows_is_accepted()
    {
        var examined = Examine(("winora.explorer.file-extensions", "0"));

        Assert.True(examined[0].IsAccepted);
        Assert.Equal(SettingsRejection.None, examined[0].Rejection);
    }

    /// <summary>
    /// A setting this build has never heard of is refused, not obeyed.
    /// </summary>
    /// <remarks>
    /// It arrives either from a newer Winora or from a hand edit. Applying an identifier nothing
    /// here defines would mean writing somewhere no catalogue vouched for, which is the one thing
    /// this program does not do.
    /// </remarks>
    [Fact]
    public void A_setting_this_build_does_not_know_is_refused()
    {
        var examined = Examine(("winora.explorer.invented-by-hand", "1"));

        Assert.False(examined[0].IsAccepted);
        Assert.Equal(SettingsRejection.Unknown, examined[0].Rejection);
    }

    /// <summary>
    /// The same setting twice is refused rather than resolved.
    /// </summary>
    /// <remarks>
    /// Taking the last would apply a value the person may never have looked at; taking the first
    /// would ignore one they may have meant. There is no reading of "twice, differently" worth
    /// guessing at.
    /// </remarks>
    [Fact]
    public void The_same_setting_twice_is_refused()
    {
        var examined = Examine(
            ("winora.shell.taskbar-alignment", "0"),
            ("winora.shell.taskbar-alignment", "1"));

        Assert.True(examined[0].IsAccepted);
        Assert.Equal(SettingsRejection.Duplicated, examined[1].Rejection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_entry_with_no_identifier_is_malformed(string id)
    {
        Assert.Equal(SettingsRejection.Malformed, Examine((id, "1"))[0].Rejection);
    }

    [Fact]
    public void An_entry_with_no_value_is_malformed()
    {
        var examined = SettingsBundle.Examine([new SettingsEntry("winora.shell.taskbar-alignment", null!)], Known);

        Assert.Equal(SettingsRejection.Malformed, examined[0].Rejection);
    }

    /// <summary>
    /// The order of the file is the order of the answer, refusals included.
    /// </summary>
    /// <remarks>
    /// Somebody comparing the file with the screen should find the same rows in the same places. A
    /// list that quietly sorted itself would make the two impossible to hold side by side.
    /// </remarks>
    [Fact]
    public void The_order_of_the_file_is_kept()
    {
        var examined = Examine(
            ("windows.appearance.theme", "dark"),
            ("winora.nothing.here", "1"),
            ("winora.shell.taskbar-alignment", "0"));

        Assert.Equal(
            ["windows.appearance.theme", "winora.nothing.here", "winora.shell.taskbar-alignment"],
            examined.Select(static c => c.Entry.OperationId));

        Assert.Equal([true, false, true], examined.Select(static c => c.IsAccepted));
    }

    [Fact]
    public void An_empty_file_asks_for_nothing()
    {
        Assert.Empty(SettingsBundle.Examine([], Known));
    }

    /// <summary>A value is carried through exactly as written.</summary>
    [Fact]
    public void The_value_is_not_touched()
    {
        Assert.Equal("dark", Examine(("windows.appearance.theme", "dark"))[0].Entry.Value);
    }
}
