using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>
/// What this copy of Winora is, and where it keeps things.
/// </summary>
/// <remarks>
/// Deliberately not a wall of switches. Winora has no preferences worth inventing: the app follows
/// the system theme, its language comes from Windows, and every behaviour that could be a setting is
/// already an explicit choice on the screen that owns it.
///
/// It used to open with a card listing the build number, whether the process was elevated, whether
/// it could apply changes at all, and the interface language — four facts, none of which anybody
/// came here to read. The owner had it removed on 2026-08-24. What is left is the two things this
/// screen exists to do: reach the appearance editor, and open the data folder.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAppEnvironment _environment;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StorageHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StorageNote { get; set; } = string.Empty;

    /// <summary>The real path. Used to open the folder, never shown.</summary>
    [ObservableProperty]
    public partial string StoragePath { get; set; } = string.Empty;

    /// <summary>
    /// The same path with the user profile folded to its variable, which is the one on screen.
    /// </summary>
    [ObservableProperty]
    public partial string StorageDisplayPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenFolderLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppearanceHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppearanceDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppearanceLabel { get; set; } = string.Empty;

    public SettingsViewModel(IAppEnvironment environment, ILocalizationService text)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Settings");
        Subtitle = _text.Get("Settings_Subtitle");
        StorageHeading = _text.Get("Settings_StorageHeading");
        StorageNote = _text.Get("Settings_StorageNote");
        StoragePath = _environment.StorageRoot;
        StorageDisplayPath = PathDisplay.Redact(_environment.StorageRoot);
        OpenFolderLabel = _text.Get("Settings_OpenFolder");
        AppearanceHeading = _text.Get("Settings_AppearanceHeading");
        AppearanceDescription = _text.Get("Settings_AppearanceDescription");
        AppearanceLabel = _text.Get("Nav_Appearance");

        return Task.CompletedTask;
    }
}
