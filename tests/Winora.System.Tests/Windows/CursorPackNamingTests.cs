using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Cards named after the download folder read as a file listing. These cases are the real folder
/// and archive names from a user's cursor folder, not invented ones.
/// </summary>
public sealed class CursorPackNamingTests
{
    [Theory]
    [InlineData("arlecchino_dd433cfc04_VSTHEMES-ORG", "Arlecchino")]
    [InlineData("glass-plane_1a8d33e03c_VSTHEMES-ORG", "Glass Plane")]
    [InlineData("rainbow-unicorn_e96f12c3ac_VSTHEMES-ORG", "Rainbow Unicorn")]
    [InlineData("mmxx_ee06b11b95_VSTHEMES-ORG", "Mmxx")]
    [InlineData("dim-v32-premium-set_56a22da788_VSTHEMES-ORG", "Dim V32 Premium")]
    [InlineData("chroma_cur_black_m_v20180130", "Chroma Black M")]
    [InlineData("chroma_cur_white_s_v20180130", "Chroma White S")]
    public void Download_names_become_readable(string raw, string expected)
    {
        Assert.Equal(expected, CursorPackNaming.Clean(raw));
    }

    /// <summary>
    /// A name Winora cannot improve is left alone. Inventing something would be worse than plain.
    /// </summary>
    [Theory]
    [InlineData("123", "123")]
    [InlineData("вариант1", "Вариант1")]
    public void A_name_with_nothing_to_strip_survives(string raw, string expected)
    {
        Assert.Equal(expected, CursorPackNaming.Clean(raw));
    }

    /// <summary>
    /// If every token looked like noise the guess was wrong, and the raw name beats an empty card.
    /// </summary>
    [Fact]
    public void A_name_made_entirely_of_noise_falls_back_to_the_original()
    {
        Assert.Equal("cursors_set", CursorPackNaming.Clean("cursors_set"));
    }

    /// <summary>Set tags are acronyms; title-casing "IR" to "Ir" reads as a typo.</summary>
    [Fact]
    public void An_acronym_set_tag_keeps_its_capitals()
    {
        Assert.Equal("Dim V32 Premium · IR", CursorPackNaming.Combine("Dim V32 Premium", "IR"));
    }

    [Fact]
    public void A_pack_with_no_set_tag_is_not_decorated()
    {
        Assert.Equal("Arlecchino", CursorPackNaming.Combine("Arlecchino", string.Empty));
    }

    [Theory]
    [InlineData("123", true)]
    [InlineData("321", true)]
    [InlineData("вариант1", true)]
    [InlineData("Новая папка", true)]
    [InlineData("New folder 2", true)]
    [InlineData("Arlecchino", false)]
    [InlineData("Chroma", false)]
    public void A_folder_name_that_says_nothing_is_recognised(string name, bool expected)
    {
        Assert.Equal(expected, CursorPackNaming.IsUninformative(name));
    }

    /// <summary>
    /// Taken from a real folder called "321" whose files were all named "neon …". The pack names
    /// itself, which beats inventing something the user cannot trace back.
    /// </summary>
    [Fact]
    public void A_shared_file_prefix_names_the_pack()
    {
        var files = new[]
        {
            "neon arrow.ani", "neon busy.ani", "neon diagonal 1.cur", "neon horizontal.cur",
        };

        Assert.Equal("neon", CursorPackNaming.CommonPrefixOf(files));
    }

    /// <summary>
    /// Files that share nothing must not produce a name. A folder of "AppStarting.ani",
    /// "Arrow.ani" shares only "A", and calling the pack "A" would be worse than leaving it.
    /// </summary>
    [Fact]
    public void Files_that_share_almost_nothing_produce_no_name()
    {
        var files = new[] { "AppStarting.ani", "Arrow.ani", "Cross.ani", "Hand.ani" };

        Assert.Equal(string.Empty, CursorPackNaming.CommonPrefixOf(files));
    }

    [Fact]
    public void A_single_file_is_not_enough_to_infer_a_name()
    {
        Assert.Equal(string.Empty, CursorPackNaming.CommonPrefixOf(["neon arrow.ani"]));
    }
}
