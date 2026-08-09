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

    /// <summary>
    /// The action button is offered only when there is something to clean and nothing in flight.
    /// Two conditions, so it is computed here rather than nested in an x:Bind, which cannot nest.
    /// </summary>
    public bool ShowAction => CanClean && !IsBusy;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    partial void OnCanCleanChanged(bool value) => OnPropertyChanged(nameof(ShowAction));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowAction));
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

    /// <summary>The reclaimable total on its own, so the page can show it as a figure.</summary>
    [ObservableProperty]
    public partial string ReclaimableValue { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReclaimableCaption { get; set; } = string.Empty;

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

        // Said once, here. The screen used to repeat it in a warning bar directly underneath.
        Subtitle = _text.Get("Cleanup_Permanent");
        ReclaimableCaption = _text.Get("Cleanup_TotalCaption");
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
                Note = NoteFor(location),
            });
        }

        ReclaimableValue = FormatBytes(totalBytes);

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
            var outcome = await _survey
                .CleanAsync(row.LocationId, CancellationToken.None)
                .ConfigureAwait(true);

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

    /// <summary>
    /// Composes the row note from two independent facts: whether Winora may touch the location at
    /// all, and what clearing it costs. They used to be one string, which meant that gaining the
    /// rights also silently dropped the warning about losing update rollback — exactly when it
    /// started to matter.
    /// </summary>
    private string NoteFor(CleanupLocationView location)
    {
        var parts = new List<string>(2);

        if (!location.IsUserOwned)
        {
            parts.Add(_text.Get("Cleanup_NeedsAdministrator"));
        }

        if (location.ReasonCode is { } reason)
        {
            var consequence = _text.Get(reason);
            if (consequence.Length > 0)
            {
                parts.Add(consequence);
            }
        }
        else if (!location.IsFullyEnumerated)
        {
            parts.Add(_text.Get("Cleanup_PartiallyRead"));
        }

        return string.Join(" ", parts);
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
