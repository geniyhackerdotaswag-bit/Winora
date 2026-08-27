using Winora.App.Services;
using Winora.Core.Profile;
using Winora.Infrastructure.Profile;
using Xunit;

namespace Winora.App.Tests.Services;

/// <summary>
/// The translation layer between <see cref="UserProfileStore"/> and the presentation layer: what
/// <see cref="ProfileService.Register"/> and <see cref="ProfileService.Save"/> do to the one file
/// on disk.
/// </summary>
public sealed class ProfileServiceTests : IDisposable
{
    private readonly string _folder;

    public ProfileServiceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-profile-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a passing test over.
        }
    }

    /// <summary>An <see cref="IActionJournalReader"/> that has nothing recorded, ever.</summary>
    private sealed class SilentJournal : IActionJournalReader
    {
        public Task<IReadOnlyList<ActionRecordView>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActionRecordView>>([]);
    }

    private ProfileService Service() =>
        new(new UserProfileStore(_folder), new ProfileMediaStore(_folder), new SilentJournal());

    /// <summary>A PNG header of the given size, written where the person would have picked it from.</summary>
    private string Picture(string name, int width, int height)
    {
        var bytes = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));

        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;

        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string MediaFolder => Path.Combine(_folder, "media");

    /// <summary>
    /// Editing a name in the cabinet must not disturb the password.
    /// </summary>
    /// <remarks>
    /// Register and Save write the same file by different routes. If Save ever stops carrying the
    /// digest across, the password is silently lost, and nobody finds out until the day there is
    /// something to log into.
    /// </remarks>
    [Fact]
    public void Saving_from_the_cabinet_keeps_the_password()
    {
        var store = new UserProfileStore(_folder);
        var service = Service();

        Assert.True(service.Register("Аня", "a@b.ru", "Password1!"));
        Assert.True(service.Save("Пётр", "a@b.ru", 3));

        var stored = store.Read();

        Assert.NotNull(stored?.Password);
        Assert.Equal("Пётр", stored.Name);
        Assert.True(PasswordHash.Verify("Password1!", stored.Password));
    }

    /// <summary>
    /// With nothing to edit, Save refuses rather than writing a profile with no password.
    /// </summary>
    /// <remarks>
    /// The dangerous case is not "no profile yet" — it is a file that is momentarily unreadable.
    /// Writing then would store an empty digest, and that file fails the store's own guard on the
    /// next launch: the whole profile is gone, and Save returned true while doing it.
    /// </remarks>
    [Fact]
    public void Saving_with_nothing_to_edit_refuses_rather_than_writing_a_passwordless_profile()
    {
        var store = new UserProfileStore(_folder);

        Assert.False(Service().Save("Аня", "a@b.ru", 0));
        Assert.Null(store.Read());
    }

    /// <summary>
    /// Correcting a typo in a name must not throw away the pictures.
    /// </summary>
    /// <remarks>
    /// Save used to build a fresh <c>UserProfile</c> from its three arguments and carry the password
    /// across by hand. Every field added after it was written would have been silently dropped by
    /// it — which is exactly what the password test above exists to catch, one field earlier.
    /// </remarks>
    [Fact]
    public void Editing_the_name_keeps_both_pictures()
    {
        var service = Service();

        Assert.True(service.Register("Аня", "a@b.ru", "Password1!"));
        Assert.Equal(
            PictureVerdict.Ok,
            service.SetPicture(ProfilePictureKind.Avatar, Picture("face.png", 256, 256)));
        Assert.Equal(
            PictureVerdict.Ok,
            service.SetPicture(ProfilePictureKind.CardBackground, Picture("wide.png", 1600, 400)));

        Assert.True(service.Save("Пётр", "a@b.ru", 3));

        var current = service.Current;

        Assert.NotEmpty(current!.AvatarImagePath);
        Assert.NotEmpty(current.BackgroundImagePath);
    }

    /// <summary>The path handed to the card is the copy, never where the person picked it from.</summary>
    [Fact]
    public void The_card_is_pointed_at_the_copy_and_not_at_the_original()
    {
        var service = Service();
        Assert.True(service.Register("Аня", "", "Password1!"));

        var source = Picture("face.png", 256, 256);

        Assert.Equal(PictureVerdict.Ok, service.SetPicture(ProfilePictureKind.Avatar, source));

        var path = service.Current!.AvatarImagePath;

        Assert.NotEqual(source, path);
        Assert.StartsWith(MediaFolder, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// The picture that was replaced does not stay on disk forever.
    /// </summary>
    /// <remarks>
    /// Nothing points at it once the profile has been rewritten, so it would be bytes in the
    /// person's profile folder that no screen could ever show and no button could ever remove.
    /// </remarks>
    [Fact]
    public void Replacing_a_picture_clears_up_after_the_old_one()
    {
        var service = Service();
        Assert.True(service.Register("Аня", "", "Password1!"));

        service.SetPicture(ProfilePictureKind.Avatar, Picture("first.png", 256, 256));
        service.SetPicture(ProfilePictureKind.Avatar, Picture("second.png", 512, 512));

        Assert.Single(Directory.GetFiles(MediaFolder));
    }

    /// <summary>
    /// Removing takes both the pointer and the file, and gives the drawn mark back.
    /// </summary>
    /// <remarks>
    /// The colour has to come back as the colour it was. A palette index thrown away when a picture
    /// was set would return as a different one, and the person would have lost something they never
    /// chose to change.
    /// </remarks>
    [Fact]
    public void A_removed_picture_gives_back_the_drawn_mark_in_its_old_colour()
    {
        var service = Service();
        Assert.True(service.Register("Аня", "", "Password1!"));
        Assert.True(service.Save("Аня", "", 4));

        var colourBefore = service.Current!.Colour;

        service.SetPicture(ProfilePictureKind.Avatar, Picture("face.png", 256, 256));

        Assert.Equal(colourBefore, service.Current!.Colour);
        Assert.True(service.RemovePicture(ProfilePictureKind.Avatar));

        Assert.Empty(service.Current!.AvatarImagePath);
        Assert.Equal(colourBefore, service.Current.Colour);
        Assert.Empty(Directory.GetFiles(MediaFolder));
    }

    /// <summary>The two pictures are independent: removing one leaves the other alone.</summary>
    [Fact]
    public void Removing_the_avatar_leaves_the_card_background()
    {
        var service = Service();
        Assert.True(service.Register("Аня", "", "Password1!"));

        service.SetPicture(ProfilePictureKind.Avatar, Picture("face.png", 256, 256));
        service.SetPicture(ProfilePictureKind.CardBackground, Picture("wide.png", 1600, 400));

        Assert.True(service.RemovePicture(ProfilePictureKind.Avatar));

        Assert.Empty(service.Current!.AvatarImagePath);
        Assert.NotEmpty(service.Current.BackgroundImagePath);
    }

    /// <summary>Removing something that was never set is not a failure to report.</summary>
    [Fact]
    public void Removing_a_picture_that_was_never_set_succeeds_quietly()
    {
        var service = Service();
        Assert.True(service.Register("Аня", "", "Password1!"));

        Assert.True(service.RemovePicture(ProfilePictureKind.Avatar));
        Assert.Empty(service.Current!.AvatarImagePath);
    }

    /// <summary>A refused file changes nothing at all, and says which rule it broke.</summary>
    [Theory]
    [InlineData(ProfilePictureKind.Avatar, 64, 64, PictureVerdict.TooSmall)]
    [InlineData(ProfilePictureKind.CardBackground, 100, 100, PictureVerdict.TooSmall)]
    public void A_refused_picture_leaves_the_profile_as_it_was(
        ProfilePictureKind kind,
        int width,
        int height,
        PictureVerdict expected)
    {
        var service = Service();
        Assert.True(service.Register("Аня", "", "Password1!"));

        Assert.Equal(expected, service.SetPicture(kind, Picture("bad.png", width, height)));

        Assert.Empty(service.Current!.AvatarImagePath);
        Assert.Empty(service.Current.BackgroundImagePath);
        Assert.False(Directory.Exists(MediaFolder));
    }

    /// <summary>
    /// A picture cannot bring a profile into being any more than an edit can.
    /// </summary>
    /// <remarks>
    /// Registration is the only way in. Writing from here with nothing to read would store an empty
    /// digest, which the store's own guard then rejects on the next launch — costing the whole
    /// profile, not just the picture.
    /// </remarks>
    [Fact]
    public void A_picture_never_creates_a_profile()
    {
        var service = Service();

        Assert.Equal(
            PictureVerdict.NotStored,
            service.SetPicture(ProfilePictureKind.Avatar, Picture("face.png", 256, 256)));

        Assert.Null(service.Current);
    }

    /// <summary>A picture whose file was deleted behind Winora's back reads as no picture.</summary>
    [Fact]
    public void A_picture_whose_file_has_gone_reads_as_no_picture()
    {
        var service = Service();
        Assert.True(service.Register("Аня", "", "Password1!"));

        service.SetPicture(ProfilePictureKind.Avatar, Picture("face.png", 256, 256));

        File.Delete(service.Current!.AvatarImagePath);

        Assert.Empty(service.Current!.AvatarImagePath);
        Assert.NotEmpty(service.Current.Initial);
    }
}
