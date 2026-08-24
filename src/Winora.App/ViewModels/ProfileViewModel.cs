using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>The personal cabinet: who you are, and what Winora has recorded of what you did.</summary>
public sealed partial class ProfileViewModel : ObservableObject
{
    /// <summary>
    /// Stored when nobody picked a colour, meaning "work it out from the name".
    /// </summary>
    /// <remarks>
    /// Taken from the core rule rather than written as -1 again. Two constants with the same value
    /// in two layers agree until the day one of them changes, and then they disagree silently.
    /// </remarks>
    public const int NoAvatarChosen = Winora.Core.Profile.ProfileAvatar.FromName;

    private readonly IProfileService _profile;
    private readonly ILocalizationService _text;

    public ProfileViewModel(IProfileService profile, ILocalizationService text)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <remarks>
    /// Partial properties, not fields: MVVMTK0045 requires this form in WinUI 3 so the CsWinRT
    /// generators can emit the WinRT marshalling code.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasProfile { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Avatar { get; set; } = NoAvatarChosen;

    [ObservableProperty]
    public partial string Colour { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Initial { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemberSince { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecordedChanges { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public string Heading => _text.Get("Profile_Heading");

    public string NameLabel => _text.Get("Profile_NameLabel");

    public string EmailLabel => _text.Get("Profile_EmailLabel");

    /// <summary>
    /// The line under the email field.
    /// </summary>
    /// <remarks>
    /// Not optional and not small print. A form shaped like a registration that sends nothing has
    /// to say so — otherwise it is the one place in Winora where the app promises something it
    /// does not do.
    /// </remarks>
    public string EmailPrivacyNote => _text.Get("Profile_EmailPrivacy");

    public string AvatarLabel => _text.Get("Profile_AvatarLabel");

    public string SaveLabel => _text.Get("Profile_Save");

    /// <summary>The palette, so the picker does not have to know how colours are chosen.</summary>
    public IReadOnlyList<string> Palette => _profile.Palette;

    public bool CanSave =>
        Winora.Core.Profile.ProfileRules.IsNameValid(Name) &&
        Winora.Core.Profile.ProfileRules.IsEmailValid(Email);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnEmailChanged(string value) => OnPropertyChanged(nameof(CanSave));

    public void Load()
    {
        var current = _profile.Current;
        HasProfile = current is not null;

        Name = current?.Name ?? _profile.SuggestedName;
        Email = current?.Email ?? string.Empty;
        Avatar = current?.Avatar ?? NoAvatarChosen;
        Colour = current?.Colour ?? string.Empty;
        Initial = current?.Initial ?? string.Empty;

        MemberSince = current is null
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                _text.Get("Profile_MemberSince"),
                current.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
    }

    public async Task LoadStatisticsAsync()
    {
        var recorded = await _profile.RecordedChangesAsync().ConfigureAwait(true);

        RecordedChanges = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Profile_RecordedChanges"),
            recorded);
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave)
        {
            return;
        }

        // Trimmed here, not just inside the service: the service is a fake in tests, and the
        // person typing around a name with stray spaces should not see them survive either way.
        var trimmedName = Winora.Core.Profile.ProfileRules.NormaliseName(Name);
        var trimmedEmail = Email.Trim();

        if (!_profile.Save(trimmedName, trimmedEmail, Avatar))
        {
            StatusMessage = _text.Get("Profile_SaveFailed");
            return;
        }

        StatusMessage = _text.Get("Profile_Saved");
        Load();
    }
}
