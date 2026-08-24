using Winora.App.Services;
using Winora.App.ViewModels;
using Xunit;

namespace Winora.App.Tests.ViewModels;

/// <summary>
/// The cabinet: who you are, and what the program has recorded of what you did.
/// </summary>
public sealed class ProfileViewModelTests
{
    private sealed class FakeProfileService : IProfileService
    {
        public ProfileView? Current { get; set; }

        public string SuggestedName { get; init; } = "brawl";

        public IReadOnlyList<string> Palette { get; } = ["#7C6BF5", "#3FA9F5", "#2FBF9E"];

        public bool SaveSucceeds { get; init; } = true;

        public (string Name, string Email, int Avatar)? LastSaved { get; private set; }

        public bool Save(string name, string email, int avatar)
        {
            LastSaved = (name, email, avatar);

            if (!SaveSucceeds)
            {
                return false;
            }

            Current = new ProfileView(name, email, avatar, DateTimeOffset.UnixEpoch, "#7C6BF5", "А");
            return true;
        }

        public bool Register(string name, string email, string password) => true;

        public Task<int> RecordedChangesAsync() => Task.FromResult(7);
    }

    private sealed class EchoLocalization : ILocalizationService
    {
        public bool IsAvailable => true;

        // The key comes back, except where a test needs a real format template.
        public string Get(string resourceKey) => resourceKey switch
        {
            "Profile_MemberSince" => "с {0}",
            "Profile_RecordedChanges" => "записано {0}",
            _ => resourceKey,
        };
    }

    private static ProfileViewModel Build(IProfileService service) =>
        new(service, new EchoLocalization());

    [Fact]
    public void Without_a_profile_there_is_nothing_to_show()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();

        Assert.False(vm.HasProfile);
    }

    [Fact]
    public void An_existing_profile_fills_the_card()
    {
        var service = new FakeProfileService
        {
            Current = new ProfileView(
                "Аня", "anya@example.com", 2,
                new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero), "#2FBF9E", "А"),
        };

        var vm = Build(service);
        vm.Load();

        Assert.True(vm.HasProfile);
        Assert.Equal("Аня", vm.Name);
        Assert.Equal("anya@example.com", vm.Email);
        Assert.Equal("#2FBF9E", vm.Colour);
        Assert.Equal("А", vm.Initial);
        Assert.Contains("2026", vm.MemberSince);
    }

    /// <summary>The button follows the rules, so a bad name cannot be saved by pressing harder.</summary>
    [Theory]
    [InlineData("", "", false)]
    [InlineData("   ", "", false)]
    [InlineData("Аня", "", true)]
    [InlineData("Аня", "a@b.ru", true)]
    [InlineData("Аня", "a@", false)]
    [InlineData("Аня", "ab.ru", false)]
    public void Saving_is_offered_only_for_a_valid_pair(string name, string email, bool expected)
    {
        var vm = Build(new FakeProfileService());
        vm.Load();
        vm.Name = name;
        vm.Email = email;

        Assert.Equal(expected, vm.CanSave);
    }

    [Fact]
    public void Saving_passes_the_trimmed_name_through()
    {
        var service = new FakeProfileService();
        var vm = Build(service);
        vm.Load();
        vm.Name = "  Аня  ";
        vm.Email = "  a@b.ru ";

        vm.SaveCommand.Execute(null);

        Assert.Equal(("Аня", "a@b.ru", ProfileViewModel.NoAvatarChosen), service.LastSaved);
    }

    [Fact]
    public void A_failed_save_says_so_and_changes_nothing()
    {
        var service = new FakeProfileService { SaveSucceeds = false };
        var vm = Build(service);
        vm.Load();
        vm.Name = "Аня";

        vm.SaveCommand.Execute(null);

        Assert.Equal("Profile_SaveFailed", vm.StatusMessage);
        Assert.False(vm.HasProfile);
    }

    [Fact]
    public void A_successful_save_says_so()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();
        vm.Name = "Аня";

        vm.SaveCommand.Execute(null);

        Assert.Equal("Profile_Saved", vm.StatusMessage);
        Assert.True(vm.HasProfile);
    }

    /// <summary>The line under the email field is not optional; it is the honest part of the form.</summary>
    [Fact]
    public void The_email_privacy_note_is_present()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();

        Assert.Equal("Profile_EmailPrivacy", vm.EmailPrivacyNote);
    }

    [Fact]
    public async Task The_recorded_change_count_comes_from_the_journal()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();

        await vm.LoadStatisticsAsync();

        Assert.Equal("записано 7", vm.RecordedChanges);
    }
}
