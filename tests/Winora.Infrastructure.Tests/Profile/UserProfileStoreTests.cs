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
        new("Аня", "anya@example.com", 2, new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero));

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
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            "{\"name\":\"   \",\"email\":\"\",\"avatar\":0,\"createdUtc\":\"2026-08-24T00:00:00+00:00\"}");

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
}
