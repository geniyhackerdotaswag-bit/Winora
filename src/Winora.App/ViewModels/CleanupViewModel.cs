using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One reclamation candidate as shown on the Cleanup screen.</summary>
public sealed partial class CleanupRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string LocationId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Path { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Size { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActionLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsProtected { get; set; }

    [ObservableProperty]
    public partial bool CanClean { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
}

/// <summary>
/// Deletes temporary files outright. There is no undo: the point of a cleaner is to free the space,
/// and a file sitting in the Recycle Bin has not freed anything. The screen says so before the user
/// acts, and reports exactly what was removed and what a running process kept hold of.
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly ICleanupSurveyService _survey;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PermanentNotice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReclaimableTotal { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public ObservableCollection<CleanupRowViewModel> Rows { get; } = [];

    public CleanupViewModel(ICleanupSurveyService survey, ILocalizationService text)
    {
        _survey = survey ?? throw new ArgumentNullException(nameof(survey));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Cleanup");
        Subtitle = _text.Get("Cleanup_Subtitle");
        PermanentNotice = _text.Get("Cleanup_Permanent");
        Rows.Clear();

        var totalBytes = 0L;
        foreach (var location in _survey.Survey(cancellationToken))
        {
            if (location.IsUserOwned)
            {
                totalBytes += location.TotalBytes ?? 0;
            }

            Rows.Add(new CleanupRowViewModel
            {
                LocationId = location.Id,
                Label = _text.Get($"Cleanup_Location_{location.Id.Replace('-', '_')}"),
                Path = location.Path,
                IsProtected = !location.IsUserOwned,
                CanClean = location.IsUserOwned && (location.FileCount ?? 0) > 0,
                ActionLabel = _text.Get("Cleanup_Action_Delete"),
                Size = location.IsUserOwned
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        _text.Get("Cleanup_SizeFormat"),
                        FormatBytes(location.TotalBytes ?? 0),
                        location.FileCount ?? 0)
                    : string.Empty,
                Note = location.ReasonCode is { } reason
                    ? _text.Get(reason)
                    : location.IsFullyEnumerated
                        ? string.Empty
                        : _text.Get("Cleanup_PartiallyRead"),
            });
        }

        ReclaimableTotal = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Cleanup_TotalFormat"),
            FormatBytes(totalBytes));

        return Task.CompletedTask;
    }

    /// <summary>Deletes one location's contents, then re-surveys so the figures stay honest.</summary>
    public async Task CleanAsync(CleanupRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsBusy || !row.CanClean)
        {
            return;
        }

        row.IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var outcome = await Task.Run(
                () => _survey.Clean(row.LocationId, CancellationToken.None)).ConfigureAwait(true);

            StatusMessage = outcome.SkippedCount > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    _text.Get("Cleanup_DoneWithSkipped"),
                    outcome.DeletedCount,
                    FormatBytes(outcome.DeletedBytes),
                    outcome.SkippedCount)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    _text.Get("Cleanup_Done"),
                    outcome.DeletedCount,
                    FormatBytes(outcome.DeletedBytes));

            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            StatusMessage = _text.Get("Cleanup_Failed");
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }
}
