using Winora.App.Services;
using Winora.App.ViewModels;
using Winora.Core.Profile;
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

        public string SuggestedName { get; init; } = "user";

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

        /// <summary>What the next SetPicture will report, so every verdict can be exercised.</summary>
        public PictureVerdict NextVerdict { get; init; } = PictureVerdict.Ok;

        public (ProfilePictureKind Kind, string Path)? LastPictureSet { get; private set; }

        public bool RemoveSucceeds { get; init; } = true;

        public PictureVerdict SetPicture(ProfilePictureKind kind, string sourcePath)
        {
            LastPictureSet = (kind, sourcePath);

            if (NextVerdict != PictureVerdict.Ok)
            {
                return NextVerdict;
            }

            var current = Current ?? Placeholder();

            Current = kind == ProfilePictureKind.Avatar
                ? current with { AvatarImagePath = sourcePath }
                : current with { BackgroundImagePath = sourcePath };

            return PictureVerdict.Ok;
        }

        public bool RemovePicture(ProfilePictureKind kind)
        {
            if (!RemoveSucceeds)
            {
                return false;
            }

            var current = Current ?? Placeholder();

            Current = kind == ProfilePictureKind.Avatar
                ? current with { AvatarImagePath = string.Empty }
                : current with { BackgroundImagePath = string.Empty };

            return true;
        }

        public Task<int> RecordedChangesAsync() => Task.FromResult(7);

        private static ProfileView Placeholder() =>
            new("Аня", string.Empty, 2, DateTimeOffset.UnixEpoch, "#2FBF9E", "А");
    }

    private sealed class EchoLocalization : ILocalizationService
    {
        public bool IsAvailable => true;

        // The key comes back, except where a test needs a real format template.
        public string Get(string resourceKey) => resourceKey switch
        {
            "Profile_MemberSince" => "с {0}",
            "Profile_RecordedChanges" => "записано {0}",

            // Spelled out rather than echoed, so the assertions below read as the words that end
            // up on the card instead of as resource keys.
            "Profile_DaysToday" => "Сегодня",
            "Profile_DaysCaption" => "Дней с Winora",
            "Profile_DaysCaptionFirst" => "Первый день с Winora",
            _ => resourceKey,
        };
    }

    /// <summary>Stands in for whatever the file dialog handed back.</summary>
    private const string ChosenFile = @"C:\pictures\chosen.png";

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

    /// <summary>A nought would be true and would read as a field nobody filled in.</summary>
    [Fact]
    public void On_the_first_day_the_figure_is_a_word()
    {
        var vm = ProfileCreated(DateTimeOffset.Now.AddHours(-3));

        Assert.Equal("Сегодня", vm.DaysWithWinora);
        Assert.Equal("Первый день с Winora", vm.DaysCaption);
    }

    [Fact]
    public void After_the_first_day_the_figure_is_the_count()
    {
        var vm = ProfileCreated(DateTimeOffset.Now.AddDays(-5));

        Assert.Equal("5", vm.DaysWithWinora);
        Assert.Equal("Дней с Winora", vm.DaysCaption);
    }

    /// <summary>
    /// A profile carried over from a machine whose clock ran ahead is dated in the future.
    /// </summary>
    [Fact]
    public void A_creation_date_in_the_future_reads_as_the_first_day()
    {
        var vm = ProfileCreated(DateTimeOffset.Now.AddDays(9));

        Assert.Equal("Сегодня", vm.DaysWithWinora);
        Assert.Equal("Первый день с Winora", vm.DaysCaption);
    }

    private static ProfileViewModel ProfileCreated(DateTimeOffset created)
    {
        var vm = Build(new FakeProfileService
        {
            Current = new ProfileView("Аня", string.Empty, 2, created, "#2FBF9E", "А"),
        });

        vm.Load();
        return vm;
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

    [Fact]
    public async Task The_recorded_change_count_comes_from_the_journal()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();

        await vm.LoadStatisticsAsync();

        Assert.Equal("записано 7", vm.RecordedChanges);
    }

    /// <summary>
    /// The rules are told at a refusal, not before it.
    /// </summary>
    /// <remarks>
    /// The screen used to state both sets of limits above their buttons, at all times, for everyone
    /// — three paragraphs of small print about pictures nobody had chosen yet. The owner had them
    /// removed on 2026-08-27. Nothing is lost, because each refusal already carries the rule it
    /// broke; this asserts that it still does, which is the whole condition for the removal being
    /// safe.
    /// </remarks>
    [Theory]
    [InlineData(PictureVerdict.TooSmall, ProfilePictureKind.Avatar, "Profile_PictureAvatarTooSmall")]
    [InlineData(PictureVerdict.TooSmall, ProfilePictureKind.CardBackground, "Profile_PictureBackgroundTooSmall")]
    [InlineData(PictureVerdict.TooLarge, ProfilePictureKind.Avatar, "Profile_PictureTooLarge")]
    [InlineData(PictureVerdict.UnsupportedFormat, ProfilePictureKind.Avatar, "Profile_PictureBadFormat")]
    public void A_refusal_names_the_rule_that_was_broken(
        PictureVerdict verdict,
        ProfilePictureKind kind,
        string expected)
    {
        var vm = Build(new FakeProfileService { NextVerdict = verdict });
        vm.Load();

        vm.ApplyPicture(kind, ChosenFile);

        var shown = kind == ProfilePictureKind.Avatar
            ? vm.AvatarPictureMessage
            : vm.BackgroundPictureMessage;

        Assert.Equal(expected, shown);
    }

    /// <summary>
    /// Every refusal says which rule it broke, and no two of them say the same thing.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is the vague one: four separate rules collapsing into a
    /// single "wrong file" that leaves the person guessing which of them they broke. Distinctness is
    /// the whole assertion - with the echo localizer, two verdicts sharing a key would show up here
    /// as two equal strings.
    /// </remarks>
    [Theory]
    [InlineData(ProfilePictureKind.Avatar)]
    [InlineData(ProfilePictureKind.CardBackground)]
    public void Every_refusal_has_its_own_words(ProfilePictureKind kind)
    {
        var messages = new List<string>();

        foreach (var verdict in Enum.GetValues<PictureVerdict>())
        {
            if (verdict == PictureVerdict.Ok)
            {
                continue;
            }

            var vm = Build(new FakeProfileService { NextVerdict = verdict });
            vm.Load();
            vm.ApplyPicture(kind, ChosenFile);

            var message = kind == ProfilePictureKind.Avatar
                ? vm.AvatarPictureMessage
                : vm.BackgroundPictureMessage;

            Assert.False(string.IsNullOrWhiteSpace(message), verdict.ToString());
            messages.Add(message);
        }

        Assert.Equal(messages.Count, messages.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The two size rules differ by place, so they cannot share one sentence.</summary>
    [Fact]
    public void An_avatar_and_a_background_are_told_about_size_differently()
    {
        var avatar = Build(new FakeProfileService { NextVerdict = PictureVerdict.TooSmall });
        avatar.Load();
        avatar.ApplyPicture(ProfilePictureKind.Avatar, ChosenFile);

        var background = Build(new FakeProfileService { NextVerdict = PictureVerdict.TooSmall });
        background.Load();
        background.ApplyPicture(ProfilePictureKind.CardBackground, ChosenFile);

        Assert.Equal("Profile_PictureAvatarTooSmall", avatar.AvatarPictureMessage);
        Assert.Equal("Profile_PictureBackgroundTooSmall", background.BackgroundPictureMessage);
    }

    /// <summary>A refusal on one picture must not appear under the other one buttons.</summary>
    [Fact]
    public void A_complaint_stays_under_the_control_it_came_from()
    {
        var vm = Build(new FakeProfileService { NextVerdict = PictureVerdict.WrongShape });
        vm.Load();

        vm.ApplyPicture(ProfilePictureKind.CardBackground, ChosenFile);

        Assert.Equal("Profile_PictureWrongShape", vm.BackgroundPictureMessage);
        Assert.Empty(vm.AvatarPictureMessage);
    }

    [Fact]
    public void An_accepted_picture_says_nothing_and_shows_itself()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();

        vm.ApplyPicture(ProfilePictureKind.Avatar, ChosenFile);

        Assert.Empty(vm.AvatarPictureMessage);
        Assert.Equal(ChosenFile, vm.AvatarImagePath);
        Assert.True(vm.HasAvatarImage);
    }

    /// <summary>A dismissed dialog is not a refusal, so nothing is said about it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_dismissed_dialog_changes_nothing(string? path)
    {
        var service = new FakeProfileService();
        var vm = Build(service);
        vm.Load();

        vm.ApplyPicture(ProfilePictureKind.Avatar, path);

        Assert.Null(service.LastPictureSet);
        Assert.Empty(vm.AvatarPictureMessage);
    }

    /// <summary>Removing is as easy as setting, or a bad photograph is a trap.</summary>
    [Fact]
    public void A_picture_can_be_taken_back_off()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();
        vm.ApplyPicture(ProfilePictureKind.Avatar, ChosenFile);

        Assert.True(vm.HasAvatarImage);

        vm.RemovePicture(ProfilePictureKind.Avatar);

        Assert.False(vm.HasAvatarImage);
        Assert.Empty(vm.AvatarImagePath);
        Assert.Empty(vm.AvatarPictureMessage);
    }

    [Fact]
    public void A_removal_that_failed_says_so()
    {
        var vm = Build(new FakeProfileService { RemoveSucceeds = false });
        vm.Load();

        vm.RemovePicture(ProfilePictureKind.CardBackground);

        Assert.Equal("Profile_PictureNotStored", vm.BackgroundPictureMessage);
    }
}
