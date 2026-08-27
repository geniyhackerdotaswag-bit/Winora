using Winora.Core.Profile;
using Winora.Infrastructure.Profile;
using Xunit;

namespace Winora.Infrastructure.Tests.Profile;

/// <summary>
/// The profile's own copies of the two pictures.
/// </summary>
/// <remarks>
/// The copying is the point. A stored path would work perfectly until the day somebody emptied
/// their Downloads folder, and then the card would lose its picture with nothing on screen to say
/// why — which is a bug reported months later as "it just stopped".
/// </remarks>
public sealed class ProfileMediaStoreTests : IDisposable
{
    private readonly string _root;

    public ProfileMediaStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "winora-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a passing test over.
        }
    }

    private ProfileMediaStore Store() => new(_root);

    private string MediaFolder => Path.Combine(_root, "media");

    /// <summary>A PNG header declaring the given size. Nothing here ever decodes one.</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));

        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;

        // Something after the header, so a copy that stopped at the part it read would be visible.
        for (var index = 24; index < bytes.Length; index++)
        {
            bytes[index] = (byte)index;
        }

        return bytes;
    }

    private string Source(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void An_accepted_picture_is_copied_into_the_profiles_own_folder()
    {
        var content = Png(512, 512);
        var outcome = Store().Save(ProfilePictureKind.Avatar, Source("chosen.png", content));

        Assert.Equal(PictureVerdict.Ok, outcome.Verdict);
        Assert.NotNull(outcome.FileName);

        var stored = Path.Combine(MediaFolder, outcome.FileName);

        Assert.True(File.Exists(stored));
        Assert.Equal(content, File.ReadAllBytes(stored));
    }

    /// <summary>The copy is a copy. Nothing is taken away from wherever the person keeps it.</summary>
    [Fact]
    public void The_file_the_person_picked_is_left_where_it_was()
    {
        var source = Source("chosen.png", Png(512, 512));

        Assert.Equal(PictureVerdict.Ok, Store().Save(ProfilePictureKind.Avatar, source).Verdict);
        Assert.True(File.Exists(source));
    }

    /// <summary>The stored name is generated, never anything the source file said about itself.</summary>
    [Fact]
    public void The_stored_name_owes_nothing_to_the_name_it_came_from()
    {
        var outcome = Store().Save(
            ProfilePictureKind.Avatar,
            Source("хитрое имя.png", Png(256, 256)));

        Assert.NotNull(outcome.FileName);
        Assert.StartsWith("avatar-", outcome.FileName, StringComparison.Ordinal);
        Assert.True(ProfilePictureRules.IsStoredFileName(outcome.FileName));
    }

    [Fact]
    public void A_card_background_is_named_for_where_it_is_going()
    {
        var outcome = Store().Save(
            ProfilePictureKind.CardBackground,
            Source("wide.png", Png(1600, 400)));

        Assert.Equal(PictureVerdict.Ok, outcome.Verdict);
        Assert.StartsWith("background-", outcome.FileName!, StringComparison.Ordinal);
    }

    /// <summary>Each refusal keeps its own name all the way up, or the screen cannot say which.</summary>
    [Theory]
    [InlineData(ProfilePictureKind.Avatar, 64, 64, PictureVerdict.TooSmall)]
    [InlineData(ProfilePictureKind.CardBackground, 400, 100, PictureVerdict.TooSmall)]
    [InlineData(ProfilePictureKind.CardBackground, 100, 100, PictureVerdict.TooSmall)]
    public void A_refusal_arrives_with_the_rule_it_broke(
        ProfilePictureKind kind,
        int width,
        int height,
        PictureVerdict expected)
    {
        var outcome = Store().Save(kind, Source("rejected.png", Png(width, height)));

        Assert.Equal(expected, outcome.Verdict);
        Assert.Null(outcome.FileName);
    }

    [Fact]
    public void An_svg_named_png_is_refused_on_its_contents()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"800\" height=\"200\"></svg>"u8.ToArray();

        var outcome = Store().Save(ProfilePictureKind.Avatar, Source("innocent.png", svg));

        Assert.Equal(PictureVerdict.UnsupportedFormat, outcome.Verdict);
    }

    [Fact]
    public void A_file_over_the_limit_is_refused()
    {
        var big = new byte[ProfilePictureRules.MaxBytes + 1];
        Png(512, 512).CopyTo(big, 0);

        var outcome = Store().Save(ProfilePictureKind.Avatar, Source("huge.png", big));

        Assert.Equal(PictureVerdict.TooLarge, outcome.Verdict);
    }

    [Fact]
    public void A_file_that_is_not_there_is_unreadable_rather_than_an_exception()
    {
        var outcome = Store().Save(
            ProfilePictureKind.Avatar,
            Path.Combine(_root, "never-existed.png"));

        Assert.Equal(PictureVerdict.Unreadable, outcome.Verdict);
    }

    /// <summary>A refused file leaves nothing behind, not even the folder.</summary>
    [Fact]
    public void Nothing_is_written_for_a_picture_that_was_turned_away()
    {
        Store().Save(ProfilePictureKind.Avatar, Source("rejected.png", Png(16, 16)));

        Assert.False(Directory.Exists(MediaFolder));
    }

    [Fact]
    public void No_temporary_file_survives_a_copy()
    {
        var outcome = Store().Save(ProfilePictureKind.Avatar, Source("chosen.png", Png(256, 256)));

        Assert.Equal(
            [outcome.FileName],
            Directory.GetFiles(MediaFolder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void A_stored_picture_can_be_found_again()
    {
        var outcome = Store().Save(ProfilePictureKind.Avatar, Source("chosen.png", Png(256, 256)));

        Assert.Equal(
            Path.Combine(MediaFolder, outcome.FileName!),
            Store().PathFor(outcome.FileName));
    }

    /// <summary>
    /// A picture whose file has gone reads as no picture, which the card draws as the initial.
    /// </summary>
    [Fact]
    public void A_name_whose_file_is_gone_points_at_nothing()
    {
        var outcome = Store().Save(ProfilePictureKind.Avatar, Source("chosen.png", Png(256, 256)));

        File.Delete(Path.Combine(MediaFolder, outcome.FileName!));

        Assert.Null(Store().PathFor(outcome.FileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..\\..\\profile.json")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("../profile.json")]
    public void A_name_that_could_leave_the_folder_points_at_nothing(string? name)
    {
        Assert.Null(Store().PathFor(name));
    }

    /// <summary>
    /// Removing is as easy as setting, or somebody who picked a bad photograph is stuck with it.
    /// </summary>
    [Fact]
    public void A_stored_picture_can_be_taken_back_out()
    {
        var outcome = Store().Save(ProfilePictureKind.Avatar, Source("chosen.png", Png(256, 256)));

        Store().Remove(outcome.FileName);

        Assert.Null(Store().PathFor(outcome.FileName));
        Assert.Empty(Directory.GetFiles(MediaFolder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..\\profile.json")]
    public void Removing_a_name_that_is_not_one_deletes_nothing(string? name)
    {
        var beside = Path.Combine(_root, "profile.json");
        File.WriteAllText(beside, "{}");

        Store().Remove(name);

        Assert.True(File.Exists(beside));
    }

    [Fact]
    public void Removing_something_that_is_already_gone_is_not_an_error()
    {
        Store().Remove("avatar-0123456789abcdef0123456789abcdef.png");
    }
}
