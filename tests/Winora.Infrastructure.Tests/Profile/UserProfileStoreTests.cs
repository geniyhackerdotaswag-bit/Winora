using Winora.Core.Profile;
using Winora.Infrastructure.Profile;
using Xunit;

namespace Winora.Infrastructure.Tests.Profile;

/// <summary>
/// Four fields on disk.
/// </summary>
/// <remarks>
/// Every failure here reads as "there is no profile yet", which sends the person to the welcome
/// window. That is the whole error policy: the profile is decoration, and a program that refused to
/// open because a decoration would not load is a worse program than one with no decoration.
/// </remarks>
public sealed class UserProfileStoreTests : IDisposable
{
    private readonly string _folder;

    public UserProfileStoreTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-profile-" + Guid.NewGuid().ToString("N"));
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

    private UserProfileStore Store() => new(_folder);

    private static UserProfile Sample() =>
        new(
            "Аня",
            "anya@example.com",
            2,
            new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero),
            PasswordHash.Create("Password1!"));

    [Fact]
    public void A_written_profile_comes_back_whole()
    {
        var written = Sample();

        Assert.True(Store().Write(written));

        var read = Store().Read();

        Assert.NotNull(read);
        Assert.Equal(written.Name, read.Name);
        Assert.Equal(written.Email, read.Email);
        Assert.Equal(written.Avatar, read.Avatar);
        Assert.Equal(written.CreatedUtc, read.CreatedUtc);
    }

    [Fact]
    public void Without_a_file_there_is_no_profile()
    {
        Assert.Null(Store().Read());
    }

    [Fact]
    public void A_missing_folder_is_not_an_error()
    {
        Assert.Null(new UserProfileStore(Path.Combine(_folder, "absent")).Read());
    }

    /// <summary>A half-written or hand-edited file reads as "not introduced yet", never as a crash.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("{\"name\":\"Аня\"")]
    [InlineData("[]")]
    public void An_unreadable_file_reads_as_no_profile(string content)
    {
        File.WriteAllText(Path.Combine(_folder, "profile.json"), content);

        Assert.Null(Store().Read());
    }

    /// <summary>
    /// A profile with no name is not a profile: the card has nothing to show and the initial has
    /// nothing to take. Treated as absent so the welcome window asks again.
    /// </summary>
    [Fact]
    public void A_profile_without_a_name_reads_as_no_profile()
    {
        // schemaVersion 2 and a usable digest, so the only reason this reads as absent is the
        // blank name — not the old-format check exercised separately below.
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            """
            {"schemaVersion":2,"name":"   ","email":"","avatar":0,
             "createdUtc":"2026-08-24T00:00:00+00:00",
             "passwordHash":"h","passwordSalt":"s","passwordIterations":600000}
            """);

        Assert.Null(Store().Read());
    }

    [Fact]
    public void Writing_twice_leaves_the_second_one()
    {
        Store().Write(Sample());
        Store().Write(Sample() with { Name = "Пётр" });

        Assert.Equal("Пётр", Store().Read()!.Name);
    }

    /// <summary>Nothing half-written is left behind beside the profile.</summary>
    [Fact]
    public void No_temporary_file_survives_a_write()
    {
        Store().Write(Sample());

        Assert.Equal(
            ["profile.json"],
            Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }

    /// <summary>A folder that does not exist yet is created rather than refused.</summary>
    [Fact]
    public void Writing_creates_the_folder()
    {
        var nested = Path.Combine(_folder, "State");

        Assert.True(new UserProfileStore(nested).Write(Sample()));
        Assert.NotNull(new UserProfileStore(nested).Read());
    }

    /// <summary>
    /// A stored name that is too long (over 32 characters) is truncated to fit, not a reason to
    /// forget everything else. The email and date are kept intact.
    /// </summary>
    [Fact]
    public void An_overlong_stored_name_is_truncated_to_fit()
    {
        var longName = "12345678901234567890123456789012345678901234"; // 44 characters
        var email = "long@example.com";
        var date = new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero);

        // schemaVersion 2 with a usable digest: a stored profile always has one now, so this test
        // of name truncation needs a valid current-format file to reach that code at all.
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            "{\"schemaVersion\":2,\"name\":\"" + longName + "\",\"email\":\"" + email +
            "\",\"avatar\":5,\"createdUtc\":\"2026-08-24T03:00:00+00:00\"," +
            "\"passwordHash\":\"h\",\"passwordSalt\":\"s\",\"passwordIterations\":600000}");

        var read = Store().Read();

        Assert.NotNull(read);
        Assert.Equal(32, read.Name.Length);
        Assert.Equal(longName[..32], read.Name);
        Assert.Equal(email, read.Email);
        Assert.Equal(date, read.CreatedUtc);
        Assert.Equal(5, read.Avatar);
    }

    /// <summary>
    /// After truncating, if the name becomes blank (which is impossible with the current logic
    /// since truncation preserves existing characters, but verify the boundary holds).
    /// </summary>
    [Fact]
    public void A_blank_name_after_normalisation_reads_as_no_profile()
    {
        // schemaVersion 2 and a usable digest, so the only reason this reads as absent is the
        // blank name — not the old-format check exercised separately below.
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            """
            {"schemaVersion":2,"name":"   ","email":"test@example.com","avatar":1,
             "createdUtc":"2026-08-24T00:00:00+00:00",
             "passwordHash":"h","passwordSalt":"s","passwordIterations":600000}
            """);

        Assert.Null(Store().Read());
    }

    /// <summary>The digest survives the round trip whole, or the password stops working.</summary>
    [Fact]
    public void The_password_digest_comes_back_whole()
    {
        var written = Sample();
        Store().Write(written);

        var read = Store().Read();

        Assert.NotNull(read?.Password);
        Assert.Equal(written.Password!.Hash, read.Password!.Hash);
        Assert.Equal(written.Password.Salt, read.Password.Salt);
        Assert.Equal(written.Password.Iterations, read.Password.Iterations);
        Assert.True(PasswordHash.Verify("Password1!", read.Password));
    }

    /// <summary>
    /// A profile from the previous version has no password, so registration was never completed.
    /// </summary>
    /// <remarks>
    /// It reads as absent rather than as a half-profile: the registration window is the only way in
    /// now, and letting an old file skip it would leave somebody with an account that has no way to
    /// be checked. Nothing of value is lost — a name and an email are half a minute to retype.
    /// </remarks>
    [Fact]
    public void A_profile_from_the_old_format_reads_as_no_profile()
    {
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            """
            {"name":"Аня","email":"anya@example.com","avatar":2,
             "createdUtc":"2026-08-24T00:00:00+00:00"}
            """);

        Assert.Null(Store().Read());
    }

    /// <summary>
    /// A version 2 profile is a whole profile that simply has no pictures yet.
    /// </summary>
    /// <remarks>
    /// This is the test that stands between a schema bump and the owner's profile. The version
    /// check used to read "older than the current version means discard", which was right while the
    /// only older version was the one written before registration existed. Version 3 added two
    /// optional picture names and nothing else; a version 2 file has a name, an address, a joining
    /// date and a password, and throwing all of it away over a feature its owner has not used would
    /// be the worst thing this store could do. Every existing profile on every machine is version 2.
    /// </remarks>
    [Fact]
    public void A_profile_from_before_pictures_existed_is_kept_whole()
    {
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            """
            {"schemaVersion":2,"name":"Аня","email":"anya@example.com","avatar":2,
             "createdUtc":"2026-08-24T00:00:00+00:00",
             "passwordHash":"h","passwordSalt":"s","passwordIterations":600000}
            """);

        var read = Store().Read();

        Assert.NotNull(read);
        Assert.Equal("Аня", read.Name);
        Assert.Equal("anya@example.com", read.Email);
        Assert.Equal(2, read.Avatar);
        Assert.Equal("h", read.Password!.Hash);
        Assert.Null(read.AvatarFile);
        Assert.Null(read.BackgroundFile);
    }

    [Fact]
    public void Picture_names_survive_the_round_trip()
    {
        var written = Sample() with
        {
            AvatarFile = "avatar-0123456789abcdef0123456789abcdef.png",
            BackgroundFile = "background-fedcba9876543210fedcba9876543210.jpg",
        };

        Assert.True(Store().Write(written));

        var read = Store().Read();

        Assert.Equal(written.AvatarFile, read!.AvatarFile);
        Assert.Equal(written.BackgroundFile, read.BackgroundFile);
    }

    /// <summary>A picture is optional, so a profile without one is not a broken profile.</summary>
    [Fact]
    public void A_profile_with_no_pictures_is_still_a_profile()
    {
        Assert.True(Store().Write(Sample()));

        var read = Store().Read();

        Assert.NotNull(read);
        Assert.Null(read.AvatarFile);
        Assert.Null(read.BackgroundFile);
    }

    /// <summary>
    /// A hand-edited picture name that could leave the media folder is dropped, not obeyed.
    /// </summary>
    /// <remarks>
    /// profile.json is plain text in the person's own folder and Winora runs elevated, so a name
    /// like <c>..\..\Windows\System32\config\SAM</c> must never be joined to a folder and followed.
    /// Dropping it costs a picture; the profile around it is untouched, because the name is
    /// decoration and the rest is not.
    /// </remarks>
    [Theory]
    [InlineData("..\\..\\Windows\\System32\\config\\SAM.png")]
    [InlineData("../../profile.json")]
    [InlineData("C:\\Windows\\notepad.png")]
    [InlineData("avatar.svg")]
    public void A_picture_name_that_is_not_a_plain_file_name_is_dropped(string name)
    {
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            "{\"schemaVersion\":3,\"name\":\"Аня\",\"email\":\"\",\"avatar\":0," +
            "\"createdUtc\":\"2026-08-24T00:00:00+00:00\"," +
            "\"passwordHash\":\"h\",\"passwordSalt\":\"s\",\"passwordIterations\":600000," +
            "\"avatarFile\":\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}");

        var read = Store().Read();

        Assert.NotNull(read);
        Assert.Equal("Аня", read.Name);
        Assert.Null(read.AvatarFile);
    }

    /// <summary>
    /// A name that got past the layer above is not written back and made to look legitimate.
    /// </summary>
    [Fact]
    public void A_picture_name_that_is_not_a_plain_file_name_is_never_written()
    {
        Assert.True(Store().Write(Sample() with { AvatarFile = "..\\escape.png" }));

        Assert.Null(Store().Read()!.AvatarFile);
        Assert.DoesNotContain("escape", File.ReadAllText(Path.Combine(_folder, "profile.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_whose_digest_is_empty_reads_as_no_profile()
    {
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            """
            {"schemaVersion":2,"name":"Аня","email":"","avatar":0,
             "createdUtc":"2026-08-24T00:00:00+00:00",
             "passwordHash":"","passwordSalt":"","passwordIterations":0}
            """);

        Assert.Null(Store().Read());
    }
}
