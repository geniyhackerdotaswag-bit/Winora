using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One fact about this installation.</summary>
public sealed partial class SettingsFactViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;
}

/// <summary>
/// What this copy of Winora is, and where it keeps things.
/// </summary>
/// <remarks>
/// Deliberately not a wall of switches. Winora has no preferences worth inventing: the app follows
/// the system theme, its language comes from Windows, and every behaviour that could be a setting is
/// already an explicit choice on the screen that owns it. What a user actually needs from here is
/// the ability to answer "which build is this, can it apply changes, and where is my data" — the
/// three questions that come up whenever something needs reporting or checking.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IDeploymentState _deployment;
    private readonly IAppEnvironment _environment;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AboutHeading { get; set; } = string.Empty;

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

    public ObservableCollection<SettingsFactViewModel> Facts { get; } = [];

    public SettingsViewModel(
        IDeploymentState deployment,
        IAppEnvironment environment,
        ILocalizationService text)
    {
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Settings");
        Subtitle = _text.Get("Settings_Subtitle");
        AboutHeading = _text.Get("Settings_AboutHeading");
        StorageHeading = _text.Get("Settings_StorageHeading");
        StorageNote = _text.Get("Settings_StorageNote");
        StoragePath = _environment.StorageRoot;
        StorageDisplayPath = PathDisplay.Redact(_environment.StorageRoot);
        OpenFolderLabel = _text.Get("Settings_OpenFolder");
        AppearanceHeading = _text.Get("Settings_AppearanceHeading");
        AppearanceDescription = _text.Get("Settings_AppearanceDescription");
        AppearanceLabel = _text.Get("Nav_Appearance");

        Facts.Clear();

        Facts.Add(new SettingsFactViewModel
        {
            Label = _text.Get("Settings_Version"),
            Value = _environment.Version,
        });

        Facts.Add(new SettingsFactViewModel
        {
            Label = _text.Get("Settings_Elevation"),
            Value = _text.Get(_environment.IsElevated
                ? "Settings_Elevation_Yes"
                : "Settings_Elevation_No"),
        });

        // The one that decides whether the app can do its job at all, so it is stated plainly
        // rather than left for the user to discover when a change silently refuses.
        Facts.Add(new SettingsFactViewModel
        {
            Label = _text.Get("Settings_CanApply"),
            Value = _deployment.CanApplyChanges
                ? _text.Get("Settings_CanApply_Yes")
                : _text.Get(_deployment.ApplyBlockReasonKey ?? "Settings_CanApply_No"),
        });

        Facts.Add(new SettingsFactViewModel
        {
            Label = _text.Get("Settings_Language"),
            Value = CultureInfo.CurrentUICulture.NativeName,
        });

        return Task.CompletedTask;
    }
}
