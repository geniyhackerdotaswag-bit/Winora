using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Services;
using Winora.Core.Profile;

namespace Winora.App.ViewModels;

/// <summary>Which of the wizard's screens is showing.</summary>
public enum RegistrationStep
{
    Name = 0,
    Email = 1,
    Password = 2,
    Done = 3,
}

/// <summary>
/// The first-run wizard: name, email, password, done.
/// </summary>
/// <remarks>
/// Knows nothing about windows or animations — it holds what has been typed and what that permits.
/// The window watches Step and moves its own pages; the model never touches a control.
/// </remarks>
public sealed partial class RegistrationViewModel : ObservableObject
{
    private readonly IProfileService _profile;
    private readonly ILocalizationService _text;

    public RegistrationViewModel(IProfileService profile, ILocalizationService text)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _text = text ?? throw new ArgumentNullException(nameof(text));

        // Offered, not imposed: the field is editable and almost always right.
        Name = _profile.SuggestedName;
    }

    /// <remarks>
    /// Partial properties, not fields: MVVMTK0045 requires this form in WinUI 3 so the CsWinRT
    /// generators can emit the WinRT marshalling code.
    /// </remarks>
    [ObservableProperty]
    public partial RegistrationStep Step { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Confirm { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public PasswordStrength Strength => PasswordStrengthRules.Evaluate(Password);

    /// <summary>Two characters, matching the reference the owner supplied.</summary>
    public bool IsNameAcceptable => ProfileRules.NormaliseName(Name).Length >= 2;

    /// <summary>
    /// Required here, unlike in the cabinet where it is optional: the reference makes it a step of
    /// its own, and a step somebody can walk past without typing is not a step.
    /// </summary>
    public bool IsEmailAcceptable =>
        Email.Trim().Length > 0 && ProfileRules.IsEmailValid(Email);

    public bool CanGoNext => Step switch
    {
        RegistrationStep.Name => IsNameAcceptable,
        RegistrationStep.Email => IsEmailAcceptable,
        _ => false,
    };

    public bool CanFinish =>
        Step == RegistrationStep.Password &&
        Strength.IsAcceptable &&
        Confirm.Length > 0 &&
        string.Equals(Password, Confirm, StringComparison.Ordinal);

    public bool CanGoBack => Step is RegistrationStep.Email or RegistrationStep.Password;

    partial void OnNameChanged(string value) => Recheck();

    partial void OnEmailChanged(string value) => Recheck();

    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(Strength));
        Recheck();
    }

    partial void OnConfirmChanged(string value) => Recheck();

    partial void OnStepChanged(RegistrationStep value) => Recheck();

    private void Recheck()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Raised once the profile exists and the app may open.</summary>
    public event EventHandler? Completed;

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext)
        {
            return;
        }

        StatusMessage = string.Empty;
        Step = Step == RegistrationStep.Name ? RegistrationStep.Email : RegistrationStep.Password;
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        StatusMessage = string.Empty;
        Step = Step == RegistrationStep.Password ? RegistrationStep.Email : RegistrationStep.Name;
    }

    [RelayCommand]
    private void Finish()
    {
        if (!CanFinish)
        {
            return;
        }

        // Trimmed here rather than left to the service: the fake in the tests records exactly what
        // it is handed, and the wizard's own contract is that what gets registered is what the
        // person meant to type, not the whitespace they left around it.
        var trimmedName = ProfileRules.NormaliseName(Name);
        var trimmedEmail = Email.Trim();

        if (!_profile.Register(trimmedName, trimmedEmail, Password))
        {
            // Stays on this step with everything typed still there: a failed save must not cost
            // somebody the three screens they just filled in.
            StatusMessage = _text.Get("Reg_SaveFailed");
            return;
        }

        // The plain text is not kept a moment longer than it takes to hash it.
        Password = string.Empty;
        Confirm = string.Empty;
        Step = RegistrationStep.Done;
    }

    [RelayCommand]
    private void Open() => Completed?.Invoke(this, EventArgs.Empty);
}
