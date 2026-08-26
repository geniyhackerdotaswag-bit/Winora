using Winora.App.Services;
using Winora.App.ViewModels;
using Winora.Core.Profile;
using Xunit;

namespace Winora.App.Tests.ViewModels;

/// <summary>
/// The wizard: three steps, and what each one refuses to let past.
/// </summary>
public sealed class RegistrationViewModelTests
{
    private sealed class FakeProfileService : IProfileService
    {
        public ProfileView? Current { get; set; }

        public string SuggestedName { get; init; } = "user";

        public IReadOnlyList<string> Palette { get; } = ["#7C6BF5"];

        // Settable, not init: the stale-status-message test flips this mid-test to prove a later
        // success clears an earlier failure's message on the same view model instance.
        public bool RegisterSucceeds { get; set; } = true;

        public (string Name, string Email, string Password)? Registered { get; private set; }

        public bool Save(string name, string email, int avatar) => true;

        public bool Register(string name, string email, string password)
        {
            Registered = (name, email, password);
            return RegisterSucceeds;
        }

        // The wizard has no pictures in it: they belong to the cabinet, which is the only place a
        // profile can be edited after it exists. Present because the interface asks for them.
        public PictureVerdict SetPicture(ProfilePictureKind kind, string sourcePath) =>
            PictureVerdict.NotStored;

        public bool RemovePicture(ProfilePictureKind kind) => false;

        public Task<int> RecordedChangesAsync() => Task.FromResult(0);
    }

    private sealed class EchoLocalization : ILocalizationService
    {
        public bool IsAvailable => true;

        public string Get(string resourceKey) => resourceKey;
    }

    private static RegistrationViewModel Build(IProfileService service) =>
        new(service, new EchoLocalization());

    [Fact]
    public void It_opens_on_the_name_step_with_the_windows_name_offered()
    {
        var vm = Build(new FakeProfileService());

        Assert.Equal(RegistrationStep.Name, vm.Step);
        Assert.Equal("user", vm.Name);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("A", false)]
    [InlineData("Ан", true)]
    [InlineData("  Аня  ", true)]
    public void The_name_step_needs_two_characters(string name, bool expected)
    {
        var vm = Build(new FakeProfileService());
        vm.Name = name;

        Assert.Equal(expected, vm.CanGoNext);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("a@", false)]
    [InlineData("ab.ru", false)]
    [InlineData("a@b.ru", true)]
    public void The_email_step_needs_an_address(string email, bool expected)
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NextCommand.Execute(null);
        vm.Email = email;

        Assert.Equal(RegistrationStep.Email, vm.Step);
        Assert.Equal(expected, vm.CanGoNext);
    }

    /// <summary>
    /// The email is required here, unlike in the cabinet where it is optional.
    /// </summary>
    /// <remarks>
    /// The reference the owner supplied makes it a required step of its own, and a step somebody
    /// can walk past without typing anything is not a step.
    /// </remarks>
    [Fact]
    public void An_empty_email_does_not_pass_the_email_step()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NextCommand.Execute(null);

        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public void Going_back_keeps_what_was_typed()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NextCommand.Execute(null);
        vm.Email = "a@b.ru";
        vm.BackCommand.Execute(null);

        Assert.Equal(RegistrationStep.Name, vm.Step);
        Assert.Equal("Аня", vm.Name);

        vm.NextCommand.Execute(null);
        Assert.Equal("a@b.ru", vm.Email);
    }

    private static RegistrationViewModel AtPasswordStep(IProfileService service)
    {
        var vm = new RegistrationViewModel(service, new EchoLocalization())
        {
            Name = "Аня",
        };

        vm.NextCommand.Execute(null);
        vm.Email = "a@b.ru";
        vm.NextCommand.Execute(null);
        return vm;
    }

    [Theory]
    [InlineData("Password1!", "Password1!", true)]
    [InlineData("Password1!", "Password1", false)]
    [InlineData("Password1!", "", false)]
    [InlineData("short1!", "short1!", false)]
    [InlineData("abcdefgh", "abcdefgh", false)]
    public void Finishing_needs_a_matching_acceptable_password(
        string password, string confirm, bool expected)
    {
        var vm = AtPasswordStep(new FakeProfileService());
        vm.Password = password;
        vm.Confirm = confirm;

        Assert.Equal(RegistrationStep.Password, vm.Step);
        Assert.Equal(expected, vm.CanFinish);
    }

    [Fact]
    public void Finishing_registers_the_trimmed_values_and_moves_to_done()
    {
        var service = new FakeProfileService();
        var vm = AtPasswordStep(service);
        vm.Name = "  Аня  ";
        vm.Email = "  a@b.ru  ";
        vm.Password = "Password1!";
        vm.Confirm = "Password1!";

        vm.FinishCommand.Execute(null);

        Assert.Equal(("Аня", "a@b.ru", "Password1!"), service.Registered);
        Assert.Equal(RegistrationStep.Done, vm.Step);
    }

    [Fact]
    public void A_failed_save_says_so_and_stays_on_the_password_step()
    {
        var vm = AtPasswordStep(new FakeProfileService { RegisterSucceeds = false });
        vm.Password = "Password1!";
        vm.Confirm = "Password1!";

        vm.FinishCommand.Execute(null);

        Assert.Equal(RegistrationStep.Password, vm.Step);
        Assert.Equal("Reg_SaveFailed", vm.StatusMessage);
    }

    [Fact]
    public void The_strength_follows_the_password()
    {
        var vm = AtPasswordStep(new FakeProfileService());
        vm.Password = "Abcdefg1!";

        Assert.Equal(4, vm.Strength.Score);
        Assert.True(vm.Strength.IsAcceptable);
    }

    [Fact]
    public void The_name_error_is_silent_until_the_field_is_touched()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "A";

        Assert.Equal(string.Empty, vm.NameError);
    }

    [Fact]
    public void The_name_error_appears_once_touched_and_still_wrong()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "A";
        vm.NameTouched = true;

        Assert.Equal("Reg_NameTooShort", vm.NameError);
    }

    [Fact]
    public void The_name_error_clears_once_touched_and_right()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NameTouched = true;

        Assert.Equal(string.Empty, vm.NameError);
    }

    [Fact]
    public void The_email_error_is_silent_until_the_field_is_touched()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NextCommand.Execute(null);
        vm.Email = "not-an-address";

        Assert.Equal(string.Empty, vm.EmailError);
    }

    [Fact]
    public void The_email_error_appears_once_touched_and_still_wrong()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NextCommand.Execute(null);
        vm.Email = "not-an-address";
        vm.EmailTouched = true;

        Assert.Equal("Reg_EmailInvalid", vm.EmailError);
    }

    [Fact]
    public void The_email_error_clears_once_touched_and_right()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NextCommand.Execute(null);
        vm.Email = "a@b.ru";
        vm.EmailTouched = true;

        Assert.Equal(string.Empty, vm.EmailError);
    }

    [Fact]
    public void A_failed_finish_leaves_the_password_and_confirm_typed_in_so_the_person_can_retry()
    {
        var vm = AtPasswordStep(new FakeProfileService { RegisterSucceeds = false });
        vm.Password = "Password1!";
        vm.Confirm = "Password1!";

        vm.FinishCommand.Execute(null);

        Assert.Equal(RegistrationStep.Password, vm.Step);
        Assert.Equal("Password1!", vm.Password);
        Assert.Equal("Password1!", vm.Confirm);
    }

    [Fact]
    public void A_stale_failure_message_does_not_survive_into_the_done_step()
    {
        var service = new FakeProfileService { RegisterSucceeds = false };
        var vm = AtPasswordStep(service);
        vm.Password = "Password1!";
        vm.Confirm = "Password1!";
        vm.FinishCommand.Execute(null);

        Assert.Equal("Reg_SaveFailed", vm.StatusMessage);

        service.RegisterSucceeds = true;
        vm.FinishCommand.Execute(null);

        Assert.Equal(RegistrationStep.Done, vm.Step);
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public void Opening_mid_wizard_raises_nothing()
    {
        var vm = Build(new FakeProfileService());
        var raised = false;
        vm.Completed += (_, _) => raised = true;

        vm.OpenCommand.Execute(null);
        Assert.False(raised);

        vm.Name = "Аня";
        vm.NextCommand.Execute(null);
        vm.OpenCommand.Execute(null);
        Assert.False(raised);

        vm.Email = "a@b.ru";
        vm.NextCommand.Execute(null);
        vm.OpenCommand.Execute(null);
        Assert.False(raised);
    }

    [Fact]
    public void Opening_after_finishing_raises_completed()
    {
        var vm = AtPasswordStep(new FakeProfileService());
        vm.Password = "Password1!";
        vm.Confirm = "Password1!";
        vm.FinishCommand.Execute(null);

        var raised = false;
        vm.Completed += (_, _) => raised = true;

        vm.OpenCommand.Execute(null);

        Assert.True(raised);
    }

    /// <summary>
    /// A fast double-click on "Открыть Winora" must not raise Completed twice: Step stays Done after
    /// the first raise, so the step check alone would let a second click through.
    /// </summary>
    [Fact]
    public void Opening_twice_raises_completed_once()
    {
        var vm = AtPasswordStep(new FakeProfileService());
        vm.Password = "Password1!";
        vm.Confirm = "Password1!";
        vm.FinishCommand.Execute(null);

        var raisedCount = 0;
        vm.Completed += (_, _) => raisedCount++;

        vm.OpenCommand.Execute(null);
        vm.OpenCommand.Execute(null);
        vm.OpenCommand.Execute(null);

        Assert.Equal(1, raisedCount);
    }
}
