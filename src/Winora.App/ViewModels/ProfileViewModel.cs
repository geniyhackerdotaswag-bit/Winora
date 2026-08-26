using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Services;
using Winora.Core.Profile;

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
    /// Whole days since the profile was created — a bare number, or a word on the first day.
    /// </summary>
    /// <remarks>
    /// Derived from the stored creation date rather than counted anywhere, so it costs nothing and
    /// cannot disagree with the "member since" line beside it. It is the second figure the cabinet
    /// has; there is no third that is not invented.
    /// </remarks>
    [ObservableProperty]
    public partial string DaysWithWinora { get; set; } = string.Empty;

    /// <summary>What the figure above means, in words.</summary>
    /// <remarks>
    /// Set beside <see cref="DaysWithWinora"/> rather than read from one fixed resource, because on
    /// the first day the figure is a word and the two have to agree. Both are written in the same
    /// place so they cannot drift apart: a caption that outlived the number it explains is the kind
    /// of thing nobody notices until it is on somebody else's screen.
    /// </remarks>
    [ObservableProperty]
    public partial string DaysCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// Where the avatar picture is, or empty for the drawn mark.
    /// </summary>
    /// <remarks>
    /// A path, and a path is all the card is given: turning it into something that can be drawn is
    /// WinUI's business, and a view model in this project holds no WinUI type. Empty rather than
    /// null because an empty string is what the card already treats as "nothing to show", and
    /// because binding an image source to null is the crash <c>ImageBindingTests</c> exists for.
    /// </remarks>
    [ObservableProperty]
    public partial string AvatarImagePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackgroundImagePath { get; set; } = string.Empty;

    /// <summary>
    /// Why the last avatar file was turned away, in words, or empty when nothing was.
    /// </summary>
    /// <remarks>
    /// One per picture rather than one shared line. Two controls that write into the same message
    /// leave the person reading a complaint about the other one.
    /// </remarks>
    [ObservableProperty]
    public partial string AvatarPictureMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackgroundPictureMessage { get; set; } = string.Empty;

    /// <summary>Whether there is an avatar picture to take back off.</summary>
    public bool HasAvatarImage => AvatarImagePath.Length > 0;

    /// <summary>Whether there is a card background to take back off.</summary>
    public bool HasBackgroundImage => BackgroundImagePath.Length > 0;

    partial void OnAvatarImagePathChanged(string value) => OnPropertyChanged(nameof(HasAvatarImage));

    partial void OnBackgroundImagePathChanged(string value) =>
        OnPropertyChanged(nameof(HasBackgroundImage));

    /// <summary>The title of the card the two pickers sit in.</summary>
    public string PicturesHeading => _text.Get("Profile_PicturesHeading");

    public string AvatarPictureLabel => _text.Get("Profile_AvatarPicture");

    /// <summary>
    /// What will be accepted, said before the dialog opens rather than after it closes.
    /// </summary>
    /// <remarks>
    /// A limit that is only ever stated as a refusal reads as the program changing its mind. These
    /// two lines are the same numbers the checker enforces, written out.
    /// </remarks>
    public string AvatarLimits => _text.Get("Profile_AvatarLimits");

    public string BackgroundPictureLabel => _text.Get("Profile_BackgroundPicture");

    public string BackgroundLimits => _text.Get("Profile_BackgroundLimits");

    public string ChoosePictureLabel => _text.Get("Profile_PictureChoose");

    public string RemovePictureLabel => _text.Get("Profile_PictureRemove");

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

        LoadPictures();

        MemberSince = current is null
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                _text.Get("Profile_MemberSince"),
                current.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture));

        // Floored at zero. A profile file carried over from a machine whose clock ran ahead would
        // otherwise put a negative number on the card, which is worse than an unremarkable nought.
        var elapsed = current is null ? 0 : (DateTimeOffset.Now - current.CreatedUtc).Days;
        var days = elapsed < 0 ? 0 : elapsed;

        // A nought is true and still reads as a field that failed to fill in, which is the one
        // thing this card exists to stop. The first day says so in words instead, and the caption
        // moves with it: "Сегодня" under "Дней с Winora" would not be a sentence.
        DaysWithWinora = current is null
            ? string.Empty
            : days == 0
                ? _text.Get("Profile_DaysToday")
                : days.ToString(CultureInfo.CurrentCulture);

        DaysCaption = days == 0
            ? _text.Get("Profile_DaysCaptionFirst")
            : _text.Get("Profile_DaysCaption");
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

    /// <summary>
    /// Takes the file the person picked and, if it is one, makes it their picture.
    /// </summary>
    /// <remarks>
    /// The dialog itself is not here: it needs a window handle, and a view model in this project
    /// has no window. The page opens it and hands the path over.
    /// </remarks>
    public void ApplyPicture(ProfilePictureKind kind, string? sourcePath)
    {
        // The dialog was dismissed. Not a refusal, so nothing is said about it.
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var verdict = _profile.SetPicture(kind, sourcePath);

        if (verdict == PictureVerdict.Ok)
        {
            LoadPictures();
        }

        SetPictureMessage(kind, MessageFor(kind, verdict));
    }

    /// <summary>Takes the picture back off, leaving the drawn mark and its colour.</summary>
    public void RemovePicture(ProfilePictureKind kind)
    {
        if (_profile.RemovePicture(kind))
        {
            LoadPictures();
            SetPictureMessage(kind, string.Empty);
            return;
        }

        SetPictureMessage(kind, MessageFor(kind, PictureVerdict.NotStored));
    }

    /// <summary>
    /// Re-reads only the two pictures.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Load"/>. Choosing a picture is one action on a screen that also holds a form,
    /// and Load puts the stored name and address back into the fields — so somebody who had typed a
    /// correction and not yet pressed Save would watch it vanish because they changed their avatar.
    /// The two are unrelated and stay unrelated.
    /// </remarks>
    private void LoadPictures()
    {
        var current = _profile.Current;

        AvatarImagePath = current?.AvatarImagePath ?? string.Empty;
        BackgroundImagePath = current?.BackgroundImagePath ?? string.Empty;
    }

    /// <summary>
    /// Which sentence goes under the control.
    /// </summary>
    /// <remarks>
    /// Every refusal names the rule it broke. The two size rules differ by place — an avatar needs
    /// both sides, a card background needs its width — so they get a sentence each rather than one
    /// that would have to be vague enough to cover both.
    /// </remarks>
    private string MessageFor(ProfilePictureKind kind, PictureVerdict verdict) => verdict switch
    {
        PictureVerdict.Ok => string.Empty,
        PictureVerdict.UnsupportedFormat => _text.Get("Profile_PictureBadFormat"),
        PictureVerdict.TooLarge => _text.Get("Profile_PictureTooLarge"),
        PictureVerdict.TooSmall => kind == ProfilePictureKind.Avatar
            ? _text.Get("Profile_PictureAvatarTooSmall")
            : _text.Get("Profile_PictureBackgroundTooSmall"),
        PictureVerdict.WrongShape => _text.Get("Profile_PictureWrongShape"),
        PictureVerdict.Unreadable => _text.Get("Profile_PictureUnreadable"),
        PictureVerdict.NotStored => _text.Get("Profile_PictureNotStored"),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    private void SetPictureMessage(ProfilePictureKind kind, string message)
    {
        if (kind == ProfilePictureKind.Avatar)
        {
            AvatarPictureMessage = message;
            return;
        }

        BackgroundPictureMessage = message;
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
