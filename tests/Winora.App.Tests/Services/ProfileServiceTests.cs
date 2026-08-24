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
        var service = new ProfileService(store, new SilentJournal());

        Assert.True(service.Register("Аня", "a@b.ru", "Password1!"));
        Assert.True(service.Save("Пётр", "a@b.ru", 3));

        var stored = store.Read();

        Assert.NotNull(stored?.Password);
        Assert.Equal("Пётр", stored.Name);
        Assert.True(PasswordHash.Verify("Password1!", stored.Password));
    }
}
