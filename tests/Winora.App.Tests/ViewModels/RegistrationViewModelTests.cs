using Winora.App.Services;
using Winora.App.ViewModels;
using Winora.Core.Licence;
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

        public (string Name, string Email)? Registered { get; private set; }

        public bool Save(string name, string email, int avatar) => true;

        public bool Register(string name, string email)
        {
            Registered = (name, email);
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

    /// <summary>
    /// Служба лицензий, которая ничего не спрашивает у сети.
    /// </summary>
    /// <remarks>
    /// Отвечает «проба началась» на всё: мастеру важно только то, что последний
    /// шаг проходится и с ключом, и без него, а разбор отказов — дело проверок
    /// самой службы, где для этого есть подставные ответы сервера.
    /// </remarks>
    private sealed class QuietLicence : ILicenceService
    {
        public LicenceState Current { get; private set; } = LicenceState.None;

        public string HardwareId => "проверочная-машина";

        public bool IsConfigured => true;

        /// <summary>Что попросили сделать в последний раз.</summary>
        public string LastAction { get; private set; } = string.Empty;

        public Task<LicenceResult> ActivateAsync(string key, string? promoCode, CancellationToken cancellationToken)
        {
            LastAction = "активация";
            Current = new LicenceState("month", DateTimeOffset.UtcNow.AddDays(30), null, DateTimeOffset.UtcNow);
            return Task.FromResult(new LicenceResult(LicenceOutcome.Activated, Current));
        }

        public Task<LicenceResult> RefreshAsync(bool force, CancellationToken cancellationToken) =>
            Task.FromResult(new LicenceResult(LicenceOutcome.Confirmed, Current));

        public Task<LicenceResult> EnsureAccessAsync(CancellationToken cancellationToken)
        {
            LastAction = "проба";
            Current = new LicenceState(LicenceState.TrialPlan, DateTimeOffset.UtcNow.AddDays(3), null, DateTimeOffset.UtcNow);
            return Task.FromResult(new LicenceResult(LicenceOutcome.Trial, Current));
        }

        public bool Forget() => true;
    }

    private static RegistrationViewModel Build(IProfileService service) =>
        new(service, new EchoLocalization(), new QuietLicence());

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

        // CanGoNext, not CanFinish: за адресом идёт ключ, и адрес открывает
        // именно переход к нему. Закончить мастера отсюда нельзя вовсе.
        Assert.Equal(expected, vm.CanGoNext);
        Assert.False(vm.CanFinish);
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

    /// <summary>
    /// Последний шаг — теперь ключ.
    /// </summary>
    /// <remarks>
    /// До него было два шага, до того — три, и третьим был пароль. Пароль
    /// собирали, хэшировали и хранили, а проверял его никто; убран 27 августа
    /// 2026 вместо того, чтобы придумывать ему вход. Третий кружок в индикаторе
    /// остался от него и теперь занят ключом.
    /// </remarks>
    private static RegistrationViewModel AtLastStep(IProfileService service)
    {
        var vm = new RegistrationViewModel(service, new EchoLocalization(), new QuietLicence())
        {
            Name = "Аня",
        };

        vm.NextCommand.Execute(null);
        vm.Email = "a@b.ru";
        vm.NextCommand.Execute(null);
        return vm;
    }

    /// <summary>
    /// Последний шаг проходится и с ключом, и без него.
    /// </summary>
    /// <remarks>
    /// Требовать ключ здесь значило бы закрыть дверь перед каждым, кто ещё не
    /// купил, — а купить он может, только посмотрев программу. Без ключа человек
    /// уходит на пробные дни, и надпись на кнопке говорит, что именно произойдёт.
    /// </remarks>
    [Theory]
    [InlineData("", "Начать пробные дни")]
    [InlineData("WNR-ABCD-EFGH-JKLM-NPQR", "Активировать")]
    public void The_key_step_finishes_with_or_without_a_key(string key, string expected)
    {
        var vm = AtLastStep(new FakeProfileService());
        vm.Key = key;

        Assert.Equal(RegistrationStep.Key, vm.Step);
        Assert.True(vm.CanFinish);
        Assert.Equal(expected == "Активировать", vm.HasKey);
        Assert.Equal(expected == "Активировать" ? "Reg_Activate" : "Reg_StartTrial", vm.FinishLabel);
    }

    /// <summary>Ключ, набранный наполовину, — это ошибка, а пустое поле — нет.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("WNR-ABC", "Licence_Malformed")]
    [InlineData("WNR-ABCD-EFGH-JKLM-NPQR", "")]
    public void A_half_typed_key_is_called_out_and_an_empty_one_is_not(string key, string expected)
    {
        var vm = AtLastStep(new FakeProfileService());
        vm.Key = key;

        Assert.Equal(expected, vm.KeyError);
    }

    /// <summary>Ключ активируется, а пустое поле уводит на пробные дни.</summary>
    [Theory]
    [InlineData("", "проба")]
    [InlineData("WNR-ABCD-EFGH-JKLM-NPQR", "активация")]
    public void The_key_decides_what_the_last_step_asks_of_the_site(string key, string expected)
    {
        var licence = new QuietLicence();
        var vm = new RegistrationViewModel(new FakeProfileService(), new EchoLocalization(), licence)
        {
            Name = "Аня",
        };

        vm.NextCommand.Execute(null);
        vm.Email = "a@b.ru";
        vm.NextCommand.Execute(null);
        vm.Key = key;

        vm.FinishCommand.Execute(null);

        Assert.Equal(expected, licence.LastAction);
        Assert.Equal(RegistrationStep.Done, vm.Step);
    }

    [Fact]
    public void Finishing_registers_the_trimmed_values_and_moves_to_done()
    {
        var service = new FakeProfileService();
        var vm = AtLastStep(service);
        vm.Name = "  Аня  ";
        vm.Email = "  a@b.ru  ";

        vm.FinishCommand.Execute(null);

        Assert.Equal(("Аня", "a@b.ru"), service.Registered);
        Assert.Equal(RegistrationStep.Done, vm.Step);
    }

    [Fact]
    /// <remarks>
    /// Everything typed stays typed: a failed save must not cost somebody the screens they filled.
    /// </remarks>
    public void A_failed_save_says_so_and_stays_on_the_last_step()
    {
        var vm = AtLastStep(new FakeProfileService { RegisterSucceeds = false });

        vm.FinishCommand.Execute(null);

        Assert.Equal(RegistrationStep.Key, vm.Step);
        Assert.Equal("Reg_SaveFailed", vm.StatusMessage);
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
    public void A_stale_failure_message_does_not_survive_into_the_done_step()
    {
        var service = new FakeProfileService { RegisterSucceeds = false };
        var vm = AtLastStep(service);
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
        var vm = AtLastStep(new FakeProfileService());
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
        var vm = AtLastStep(new FakeProfileService());
        vm.FinishCommand.Execute(null);

        var raisedCount = 0;
        vm.Completed += (_, _) => raisedCount++;

        vm.OpenCommand.Execute(null);
        vm.OpenCommand.Execute(null);
        vm.OpenCommand.Execute(null);

        Assert.Equal(1, raisedCount);
    }
}
