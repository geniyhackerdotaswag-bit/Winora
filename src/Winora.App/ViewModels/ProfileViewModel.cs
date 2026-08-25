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

    /// <summary>
    /// The same count, as a bare number.
    /// </summary>
    /// <remarks>
    /// The card sets it large with a caption underneath rather than as a sentence, which is also
    /// how it dodges Russian numeral agreement: a caption states the category and never has to
    /// agree with the figure above it, so there is no branch on 1, on 2 to 4, and on the rest.
    /// </remarks>
    [ObservableProperty]
    public partial string RecordedChangesValue { get; set; } = string.Empty;

    /// <summary>
    /// Whole days since the profile was created, as a bare number.
    /// </summary>
    /// <remarks>
    /// Derived from the stored creation date rather than counted anywhere, so it costs nothing and
    /// cannot disagree with the "member since" line beside it. It is the second figure the cabinet
    /// has; there is no third that is not invented.
    /// </remarks>
    [ObservableProperty]
    public partial string DaysWithWinora { get; set; } = string.Empty;

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

    public string Subtitle => _text.Get("Profile_Subtitle");

    /// <summary>The title in the header strip of the card the fields sit in.</summary>
    public string DetailsHeading => _text.Get("Profile_DetailsHeading");

    public string NamePlaceholder => _text.Get("Profile_NamePlaceholder");

    public string EmailPlaceholder => _text.Get("Profile_EmailPlaceholder");

    public string ChangesCaption => _text.Get("Profile_ChangesCaption");

    public string DaysCaption => _text.Get("Profile_DaysCaption");

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

        // Floored at zero. A profile file carried over from a machine whose clock ran ahead would
        // otherwise put a negative number on the card, which is worse than an unremarkable nought.
        var days = current is null ? 0 : (DateTimeOffset.Now - current.CreatedUtc).Days;

        DaysWithWinora = current is null
            ? string.Empty
            : (days < 0 ? 0 : days).ToString(CultureInfo.CurrentCulture);
    }

    public async Task LoadStatisticsAsync()
    {
        var recorded = await _profile.RecordedChangesAsync().ConfigureAwait(true);

        RecordedChanges = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Profile_RecordedChanges"),
            recorded);

        RecordedChangesValue = recorded.ToString(CultureInfo.CurrentCulture);
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
