using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Matching a file name to a cursor role is a guess, so it is tested where the guess is most likely
/// to be wrong: short tokens that appear inside longer words, and the two diagonals.
/// </summary>
public sealed class CursorFolderScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winora-cursor-folder").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    [Theory]
    [InlineData("normal_select.ani", CursorRole.Arrow)]
    [InlineData("Pointer.cur", CursorRole.Arrow)]
    [InlineData("help_select.ani", CursorRole.Help)]
    [InlineData("working_in_background.ani", CursorRole.AppStarting)]
    [InlineData("busy.ani", CursorRole.Wait)]
    [InlineData("precision_select.cur", CursorRole.Crosshair)]
    [InlineData("text_select.cur", CursorRole.IBeam)]
    [InlineData("handwriting.cur", CursorRole.NWPen)]
    [InlineData("unavailable.cur", CursorRole.No)]
    [InlineData("vertical_resize.cur", CursorRole.SizeNS)]
    [InlineData("horizontal_resize.cur", CursorRole.SizeWE)]
    [InlineData("move.ani", CursorRole.SizeAll)]
    [InlineData("alternate_select.cur", CursorRole.UpArrow)]
    [InlineData("link_select.ani", CursorRole.Hand)]
    public void Common_pack_names_resolve_to_their_role(string fileName, CursorRole expected)
    {
        Assert.Equal(expected, CursorFolderScanner.RoleForFileName(fileName));
    }

    /// <summary>
    /// The pair that must not be swapped. Getting these the wrong way round is invisible until a
    /// user notices their resize arrows lean the wrong way.
    /// </summary>
    [Theory]
    [InlineData("diagonal_resize1.ani", CursorRole.SizeNWSE)]
    [InlineData("diagonal_resize2.ani", CursorRole.SizeNESW)]
    [InlineData("size_nwse.cur", CursorRole.SizeNWSE)]
    [InlineData("size_nesw.cur", CursorRole.SizeNESW)]
    public void The_two_diagonals_are_told_apart(string fileName, CursorRole expected)
    {
        Assert.Equal(expected, CursorFolderScanner.RoleForFileName(fileName));
    }

    /// <summary>
    /// "no" is inside "normal" and "up" is inside "unsupported". A naive substring pass would
    /// classify the ordinary pointer as the unavailable cursor, which is the single most visible way
    /// this could go wrong.
    /// </summary>
    [Theory]
    [InlineData("normal.cur", CursorRole.Arrow)]
    [InlineData("normal_select.ani", CursorRole.Arrow)]
    [InlineData("no.cur", CursorRole.No)]
    [InlineData("up.cur", CursorRole.UpArrow)]
    public void Short_tokens_do_not_hijack_longer_names(string fileName, CursorRole expected)
    {
        Assert.Equal(expected, CursorFolderScanner.RoleForFileName(fileName));
    }

    /// <summary>
    /// The abbreviated naming a real pack used. Its normal-select cursor is called "default", which
    /// nothing matched — so the pack listed eight roles, showed no preview, and applying it left the
    /// pointer untouched. A pack whose main cursor is unrecognised is a pack that silently does
    /// nothing, which is worse than one that is obviously missing.
    /// </summary>
    [Theory]
    [InlineData("default.ani", CursorRole.Arrow)]
    [InlineData("default_s.ani", CursorRole.Arrow)]
    [InlineData("dg1.ani", CursorRole.SizeNWSE)]
    [InlineData("dg2.ani", CursorRole.SizeNESW)]
    [InlineData("horz.ani", CursorRole.SizeWE)]
    [InlineData("vert.ani", CursorRole.SizeNS)]
    [InlineData("work.ani", CursorRole.AppStarting)]
    public void Abbreviated_pack_names_are_recognised(string fileName, CursorRole expected)
    {
        Assert.Equal(expected, CursorFolderScanner.RoleForFileName(fileName));
    }

    /// <summary>
    /// Имена, которыми роли зовёт сама Windows.
    /// </summary>
    /// <remarks>
    /// Второй лагерь наборов подписывает файлы не словами человека, а именами ролей
    /// из Win32: <c>SizeAll.ani</c>, <c>SizeNS.ani</c>, <c>IBeam.ani</c>. Таблица их
    /// не знала, и такой набор ставился наполовину — молча, потому что нераспознанный
    /// файл просто пропускается. Нашлось на живом наборе «Терракота».
    /// </remarks>
    [Theory]
    [InlineData("SizeAll.ani", CursorRole.SizeAll)]
    [InlineData("SizeNS.ani", CursorRole.SizeNS)]
    [InlineData("SizeWE.ani", CursorRole.SizeWE)]
    [InlineData("SizeNWSE.ani", CursorRole.SizeNWSE)]
    [InlineData("SizeNESW.ani", CursorRole.SizeNESW)]
    [InlineData("IBeam.ani", CursorRole.IBeam)]
    [InlineData("AppStarting.ani", CursorRole.AppStarting)]
    [InlineData("NO.ani", CursorRole.No)]
    [InlineData("Cross.ani", CursorRole.Crosshair)]
    [InlineData("Hand.ani", CursorRole.Hand)]
    [InlineData("Wait.ani", CursorRole.Wait)]
    [InlineData("Help.ani", CursorRole.Help)]
    [InlineData("Handwriting.ani", CursorRole.NWPen)]
    public void The_names_Windows_itself_uses_are_recognised(string fileName, CursorRole expected)
    {
        Assert.Equal(expected, CursorFolderScanner.RoleForFileName(fileName));
    }

    /// <summary>
    /// Стрелка вверх и обычный указатель — не одно и то же.
    /// </summary>
    /// <remarks>
    /// «uparrow» содержит «arrow» целиком. Пока «arrow» стояла в таблице выше,
    /// <c>UpArrow.ani</c> объявлялся обычным указателем — а тот в наборе уже есть,
    /// и одна запись затирала другую: набор терял и стрелку вверх, и половину
    /// шансов на то, что указателем окажется именно <c>Arrow.ani</c>.
    /// </remarks>
    [Theory]
    [InlineData("UpArrow.ani", CursorRole.UpArrow)]
    [InlineData("Arrow.ani", CursorRole.Arrow)]
    public void An_up_arrow_is_not_swallowed_by_the_plain_arrow(string fileName, CursorRole expected)
    {
        Assert.Equal(expected, CursorFolderScanner.RoleForFileName(fileName));
    }

    /// <summary>
    /// Pack authors misspell, and the stem has to survive it. Both spellings appear in the wild.
    /// </summary>
    [Theory]
    [InlineData("unavailable.ani", CursorRole.No)]
    [InlineData("unavailiable.ani", CursorRole.No)]
    public void A_misspelled_unavailable_cursor_still_matches(string fileName, CursorRole expected)
    {
        Assert.Equal(expected, CursorFolderScanner.RoleForFileName(fileName));
    }

    /// <summary>The full spellings must keep working now that the table holds stems.</summary>
    [Theory]
    [InlineData("vertical.ani", CursorRole.SizeNS)]
    [InlineData("horizontal.ani", CursorRole.SizeWE)]
    [InlineData("working.ani", CursorRole.AppStarting)]
    public void Full_spellings_still_match_after_shortening_the_tokens(string fileName, CursorRole expected)
    {
        Assert.Equal(expected, CursorFolderScanner.RoleForFileName(fileName));
    }

    [Fact]
    public void An_unrecognisable_name_matches_nothing()
    {
        Assert.Null(CursorFolderScanner.RoleForFileName("cursor_07.cur"));
    }

    /// <summary>
    /// The exclusion that makes the drop folder safe. An installer script is the dangerous part of a
    /// downloaded pack, and Winora runs elevated.
    /// </summary>
    [Fact]
    public void Only_cursor_files_are_read_and_installers_are_ignored()
    {
        var pack = Path.Combine(_root, "Neon");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "normal_select.cur"), "x");
        File.WriteAllText(Path.Combine(pack, "install.inf"), "[Version]");
        File.WriteAllText(Path.Combine(pack, "setup.exe"), "MZ");
        File.WriteAllText(Path.Combine(pack, "scheme.reg"), "REGEDIT4");

        var found = Assert.Single(new CursorFolderScanner(_root).Packs());

        Assert.Equal("Neon", found.Name);
        Assert.Single(found.Files);
        Assert.All(
            found.Files.Values.Concat(found.UnmatchedFileNames),
            entry => Assert.DoesNotContain(".inf", entry, StringComparison.OrdinalIgnoreCase));
        Assert.All(
            found.Files.Values.Concat(found.UnmatchedFileNames),
            entry => Assert.DoesNotContain(".exe", entry, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_file_whose_role_is_unknown_is_reported_rather_than_guessed()
    {
        var pack = Path.Combine(_root, "Mixed");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "normal_select.cur"), "x");
        File.WriteAllText(Path.Combine(pack, "cursor_99.cur"), "x");

        var found = Assert.Single(new CursorFolderScanner(_root).Packs());

        Assert.Single(found.Files);
        Assert.Equal("cursor_99.cur", Assert.Single(found.UnmatchedFileNames));
    }

    [Fact]
    public void A_folder_with_no_cursor_files_is_not_offered_as_a_pack()
    {
        var pack = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "readme.txt"), "hello");

        Assert.Empty(new CursorFolderScanner(_root).Packs());
    }

    [Fact]
    public void The_root_folder_is_created_so_the_user_can_find_it()
    {
        var root = Path.Combine(_root, "made-on-demand");

        var packs = new CursorFolderScanner(root).Packs();

        Assert.Empty(packs);
        Assert.True(Directory.Exists(root));
    }
}
