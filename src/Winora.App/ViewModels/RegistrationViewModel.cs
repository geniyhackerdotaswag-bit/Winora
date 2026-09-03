using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Services;
using Winora.Core.Licence;
using Winora.Core.Profile;

namespace Winora.App.ViewModels;

/// <summary>Which of the wizard's screens is showing.</summary>
public enum RegistrationStep
{
    Name = 0,
    Email = 1,
    Key = 2,
    Done = 3,
}

/// <summary>
/// The first-run wizard: name, email, done.
/// </summary>
/// <remarks>
/// Knows nothing about windows or animations — it holds what has been typed and what that permits.
/// The window watches Step and moves its own pages; the model never touches a control.
/// </remarks>
public sealed partial class RegistrationViewModel : ObservableObject
{
    private readonly IProfileService _profile;
    private readonly ILocalizationService _text;
    private readonly ILicenceService _licence;

    public RegistrationViewModel(
        IProfileService profile,
        ILocalizationService text,
        ILicenceService licence)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _licence = licence ?? throw new ArgumentNullException(nameof(licence));

        // Offered, not imposed: the field is editable and almost always right.
        Name = _profile.SuggestedName;
    }

    /// <remarks>
    /// Partial properties, not fields: MVVMTK0045 requires this form in WinUI 3 so the CsWinRT
    /// generators can emit the WinRT marshalling code.
    /// </remarks>
    [ObservableProperty]
    public partial RegistrationStep Step { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    /// <summary>Ключ, если он у человека есть.</summary>
    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PromoCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }



    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// Whether the person has left each field, so a message appears after they typed rather than
    /// while they are still typing the first letter.
    /// </summary>
    /// <remarks>
    /// The reference the owner supplied does the same: a field is not scolded for being incomplete
    /// until somebody has moved on from it.
    /// </remarks>
    [ObservableProperty]
    public partial bool NameTouched { get; set; }

    [ObservableProperty]
    public partial bool EmailTouched { get; set; }


    /// <summary>Two characters, matching the reference the owner supplied.</summary>
    public bool IsNameAcceptable => ProfileRules.NormaliseName(Name).Length >= 2;

    /// <summary>
    /// Required here, unlike in the cabinet where it is optional: the reference makes it a step of
    /// its own, and a step somebody can walk past without typing is not a step.
    /// </summary>
    public bool IsEmailAcceptable =>
        Email.Trim().Length > 0 && ProfileRules.IsEmailValid(Email);

    /// <summary>What is wrong with the name, or empty when nothing is.</summary>
    public string NameError =>
        NameTouched && !IsNameAcceptable ? _text.Get("Reg_NameTooShort") : string.Empty;

    /// <summary>What is wrong with the email, or empty when nothing is.</summary>
    public string EmailError =>
        EmailTouched && !IsEmailAcceptable ? _text.Get("Reg_EmailInvalid") : string.Empty;

    public bool CanGoNext => Step switch
    {
        RegistrationStep.Name => IsNameAcceptable,
        RegistrationStep.Email => IsEmailAcceptable,
        _ => false,
    };

    /*
     * Последний шаг проходится всегда, с ключом или без.
     *
     * Заставить ввести ключ здесь значило бы потерять каждого, кто ещё не купил,
     * — а купить он может, только увидев программу. Без ключа человек уходит на
     * пробные дни, и надпись на кнопке говорит, что именно сейчас произойдёт.
     */
    public bool CanFinish => Step == RegistrationStep.Key && !IsBusy;

    /// <summary>Похоже ли набранное на ключ. Пустое поле — это не ошибка.</summary>
    public bool HasKey => LicenceKey.IsWellFormed(Key);

    /// <summary>Что написано на кнопке последнего шага.</summary>
    public string FinishLabel =>
        HasKey ? _text.Get("Reg_Activate") : _text.Get("Reg_StartTrial");

    /// <summary>Что не так с ключом, или пусто.</summary>
    public string KeyError =>
        Key.Length > 0 && !HasKey ? _text.Get("Licence_Malformed") : string.Empty;

    public bool CanGoBack => Step is RegistrationStep.Email or RegistrationStep.Key;

    partial void OnNameChanged(string value) => Recheck();

    partial void OnEmailChanged(string value) => Recheck();

    partial void OnKeyChanged(string value) => Recheck();

    partial void OnIsBusyChanged(bool value) => Recheck();

    partial void OnStepChanged(RegistrationStep value) => Recheck();

    partial void OnNameTouchedChanged(bool value) => Recheck();

    partial void OnEmailTouchedChanged(bool value) => Recheck();

    private void Recheck()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(EmailError));
        OnPropertyChanged(nameof(KeyError));
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(FinishLabel));
    }

    /// <summary>Raised once the profile exists and the app may open.</summary>
    public event EventHandler? Completed;

    /// <summary>
    /// Whether <see cref="Completed"/> has already been raised.
    /// </summary>
    /// <remarks>
    /// <see cref="Step"/> stays <see cref="RegistrationStep.Done"/> after the first raise, so
    /// <see cref="Open"/>'s step check alone does not stop a second click — a fast double press on
    /// "Открыть Winora" would raise <see cref="Completed"/> twice. Its subscriber in
    /// <c>App.xaml.cs</c> constructs and activates a second <c>MainWindow</c> and closes the
    /// registration window a second time on the second raise, which is worth refusing here where the
    /// state that decides it already lives, rather than leaving every subscriber to guard itself.
    /// </remarks>
    private bool _completed;

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext)
        {
            return;
        }

        StatusMessage = string.Empty;
        Step = Step == RegistrationStep.Name ? RegistrationStep.Email : RegistrationStep.Key;
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        StatusMessage = string.Empty;
        Step = Step == RegistrationStep.Key ? RegistrationStep.Email : RegistrationStep.Name;
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        if (!CanFinish)
        {
            return;
        }

        IsBusy = true;

        try
        {
            // Trimmed here rather than left to the service: the fake in the tests records exactly
            // what it is handed, and the wizard's own contract is that what gets registered is what
            // the person meant to type, not the whitespace they left around it.
            var trimmedName = ProfileRules.NormaliseName(Name);
            var trimmedEmail = Email.Trim();

            if (!_profile.Register(trimmedName, trimmedEmail))
            {
                // Stays on this step with everything typed still there: a failed save must not cost
                // somebody the three screens they just filled in.
                StatusMessage = _text.Get("Reg_SaveFailed");
                return;
            }

            /*
             * Ключ — после профиля, и его отказ шага не отменяет.
             *
             * Профиль уже сохранён, и возвращать человека к имени из-за того, что
             * сайт не ответил, было бы наказанием за чужую неполадку. Отказ
             * показывается словами, а войти он сможет — на пробных днях или
             * введя ключ позже, в разделе «Подписка».
             */
            var access = HasKey
                ? await _licence.ActivateAsync(Key, PromoCode, CancellationToken.None).ConfigureAwait(true)
                : await _licence.EnsureAccessAsync(CancellationToken.None).ConfigureAwait(true);

            StatusMessage = access.Succeeded || access.Outcome is LicenceOutcome.Trial
                ? string.Empty
                : _text.Get(MessageKeyFor(access.Outcome));

            Step = RegistrationStep.Done;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Строка для отказа, полученного на последнем шаге.
    /// </summary>
    /// <remarks>
    /// Те же слова, что на экране «Подписка»: человек увидит их дважды — здесь и
    /// потом там, — и две разные формулировки одного отказа читались бы как две
    /// разные неполадки.
    /// </remarks>
    private static string MessageKeyFor(LicenceOutcome outcome) => outcome switch
    {
        LicenceOutcome.Malformed => "Licence_Malformed",
        LicenceOutcome.Rejected => "Licence_Rejected",
        LicenceOutcome.Expired => "Licence_Expired",
        LicenceOutcome.DeviceLimit => "Licence_DeviceLimit",
        LicenceOutcome.OtherMachine => "Licence_OtherMachine",
        LicenceOutcome.TrialUsed => "Licence_TrialOver",
        LicenceOutcome.NotConfigured => "Licence_NotConfigured",
        _ => "Licence_Unreachable",
    };

    [RelayCommand]
    private void Open()
    {
        // Only from the last step. The window hangs the app's opening on this event, and raising it
        // early would show the shell over an unfinished registration. The second guard is for the
        // step this one cannot catch: Step remains Done after the first raise, so nothing here would
        // otherwise stop a second click from raising Completed again — see _completed's remarks.
        if (Step != RegistrationStep.Done || _completed)
        {
            return;
        }

        _completed = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
