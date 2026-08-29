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

    [ObservableProperty]
    public partial string OpenFolderLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppearanceHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppearanceDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppearanceLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TransferHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TransferDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TransferSaveLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TransferLoadLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TransferMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsTransferBusy { get; set; }

    private readonly ISettingsTransferService _transfer;

    public SettingsViewModel(
        IAppEnvironment environment,
        ILocalizationService text,
        ISettingsTransferService transfer)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Settings");
        Subtitle = _text.Get("Settings_Subtitle");
        StorageHeading = _text.Get("Settings_StorageHeading");
        StorageNote = _text.Get("Settings_StorageNote");
        StoragePath = _environment.StorageRoot;
        OpenFolderLabel = _text.Get("Settings_OpenFolder");
        TransferHeading = _text.Get("Settings_TransferHeading");
        TransferDescription = string.Format(
            global::System.Globalization.CultureInfo.CurrentCulture,
            _text.Get("Settings_TransferDescription"),
            _transfer.PortableCount);
        TransferSaveLabel = _text.Get("Settings_TransferSave");
        TransferLoadLabel = _text.Get("Settings_TransferLoad");

        AppearanceHeading = _text.Get("Settings_AppearanceHeading");
        AppearanceDescription = _text.Get("Settings_AppearanceDescription");
        AppearanceLabel = _text.Get("Nav_Appearance");

        return Task.CompletedTask;
    }

    /// <summary>Writes this machine's settings to a file the person chose.</summary>
    public async Task SaveTransferAsync(string path)
    {
        IsTransferBusy = true;

        try
        {
            var saved = await _transfer.SaveAsync(path).ConfigureAwait(true);
            var captured = saved ? (await _transfer.CaptureAsync().ConfigureAwait(true)).Count : 0;

            TransferMessage = saved
                ? string.Format(
                    global::System.Globalization.CultureInfo.CurrentCulture,
                    _text.Get("Settings_TransferSaved"),
                    captured)
                : _text.Get("Settings_TransferSaveFailed");
        }
        finally
        {
            IsTransferBusy = false;
        }
    }

    /// <summary>
    /// Applies a file to this machine, and says exactly what happened to every entry in it.
    /// </summary>
    /// <remarks>
    /// Four numbers rather than one: applied, already the same, refused and failed mean different
    /// things, and rolling them into "готово" would hide the two that somebody has to act on.
    /// </remarks>
    public async Task LoadTransferAsync(string path)
    {
        IsTransferBusy = true;

        try
        {
            var report = await _transfer.ApplyAsync(path).ConfigureAwait(true);

            if (report is null)
            {
                TransferMessage = _text.Get("Settings_TransferNotAFile");
                return;
            }

            var parts = new List<string>
            {
                string.Format(
                    global::System.Globalization.CultureInfo.CurrentCulture,
                    _text.Get("Settings_TransferApplied"),
                    report.Applied),
            };

            if (report.Unchanged > 0)
            {
                parts.Add(string.Format(
                    global::System.Globalization.CultureInfo.CurrentCulture,
                    _text.Get("Settings_TransferUnchanged"),
                    report.Unchanged));
            }

            if (report.Refused.Count > 0)
            {
                parts.Add(string.Format(
                    global::System.Globalization.CultureInfo.CurrentCulture,
                    _text.Get("Settings_TransferRefused"),
                    report.Refused.Count));
            }

            if (report.Failed.Count > 0)
            {
                parts.Add(string.Format(
                    global::System.Globalization.CultureInfo.CurrentCulture,
                    _text.Get("Settings_TransferFailed"),
                    report.Failed.Count));
            }

            TransferMessage = string.Join(", ", parts) + ".";
        }
        finally
        {
            IsTransferBusy = false;
        }
    }
}
