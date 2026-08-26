using Winora.Core.Changes;
using Xunit;

namespace Winora.Core.Tests.Changes;

/// <summary>
/// The change history is the screen that has to be believed. Everything here exists so it stops
/// showing its own plumbing — and so it never invents a name it does not have.
/// </summary>
public sealed class ChangeCaptionTests
{
    /// <summary>
    /// The names actually found in this machine's startup list on 26 August 2026, alongside the
    /// ones that gave the code its shape.
    /// </summary>
    [Theory]
    [InlineData(
        "MicrosoftEdgeAutoLaunch_48EDF2D71EE0AC2F5D41DCF1908B2D8F",
        "Microsoft Edge")]
    [InlineData(
        "GoogleChromeAutoLaunch_B6F77B549AFE9788D13F403C273D0139",
        "Google Chrome")]
    [InlineData("OneDriveAutoLaunch_0011", "One Drive")]
    public void The_per_install_identifier_is_dropped_and_the_name_reads(string stored, string expected)
    {
        Assert.Equal(expected, ChangeCaption.Readable(stored));
    }

    /// <summary>A run of capitals stays whole rather than becoming one letter per word.</summary>
    [Theory]
    [InlineData("EADMAutoLaunch_1", "EADM")]
    [InlineData("FACEITAutoLaunch_1", "FACEIT")]
    [InlineData("VPNClientAutoLaunch_1", "VPN Client")]
    public void Capitals_that_belong_together_stay_together(string stored, string expected)
    {
        Assert.Equal(expected, ChangeCaption.Readable(stored));
    }

    /// <summary>
    /// An acronym buried inside a word cannot be recovered, and this does not pretend otherwise.
    /// </summary>
    /// <remarks>
    /// "PoEOverlay" splits to "Po E Overlay", because a capital after a lowercase letter is exactly
    /// what makes "MicrosoftEdge" into two words. No rule separates the two cases without knowing
    /// the product. Written down rather than papered over: the names this actually meets are
    /// browsers and Electron applications registering themselves, and the one real entry of that
    /// shape on this machine — "electron.app.PoE Overlay II Standalone" — carries its own spaces
    /// and never reaches here at all.
    /// </remarks>
    [Fact]
    public void An_acronym_inside_a_word_is_split_wrongly_and_that_is_known()
    {
        Assert.Equal("Po E Overlay", ChangeCaption.Readable("PoEOverlayAutoLaunch_1"));
    }

    /// <summary>
    /// A title this does not recognise comes back exactly as it went in.
    /// </summary>
    /// <remarks>
    /// A raw name is honest and a guessed one is not. On a screen about undoing changes, a name
    /// that is wrong is worse than a name that is ugly.
    /// </remarks>
    [Theory]
    [InlineData("Группировка на других экранах")]
    [InlineData("Docker Desktop")]
    [InlineData("Battle.net")]
    [InlineData("Кнопка «Представление задач»")]
    public void A_title_that_is_already_a_name_is_left_alone(string stored)
    {
        Assert.Equal(stored, ChangeCaption.Readable(stored));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Nothing_stored_reads_as_nothing(string? stored, string expected)
    {
        Assert.Equal(expected, ChangeCaption.Readable(stored));
    }

    /// <summary>
    /// The marker at the very start leaves no name in front of it, so there is nothing to shorten
    /// and the value is passed through whole.
    /// </summary>
    [Fact]
    public void A_name_that_is_only_the_marker_is_left_alone()
    {
        Assert.Equal("AutoLaunch_99", ChangeCaption.Readable("AutoLaunch_99"));
    }

    [Theory]
    [InlineData("enabled → disabled", "enabled", "disabled")]
    [InlineData("unset → 2", "unset", "2")]
    [InlineData("1 → 0", "1", "0")]
    [InlineData("  on   →   off  ", "on", "off")]
    public void The_two_sides_of_the_arrow_are_read_apart(string summary, string before, string after)
    {
        Assert.Equal(before, ChangeCaption.Before(summary));
        Assert.Equal(after, ChangeCaption.After(summary));
    }

    /// <summary>
    /// A plan with no steps stores an empty summary, and a change from a build that wrote them
    /// differently would have no arrow either. Both give nothing rather than half a sentence.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("applied")]
    [InlineData("a → b → c")]
    public void A_summary_without_exactly_one_arrow_gives_nothing(string? summary)
    {
        Assert.Equal(string.Empty, ChangeCaption.Before(summary));
        Assert.Equal(string.Empty, ChangeCaption.After(summary));
    }
}
