using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One reclamation candidate as shown on the Cleanup screen.</summary>
public sealed partial class CleanupRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Path { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Size { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsProtected { get; set; }
}

/// <summary>
/// Shows what reclamation would consider, and states plainly that reclaiming is not available yet.
/// Read-only by construction: this screen has no path that moves or deletes anything.
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
    public partial string NotAvailableNotice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReclaimableTotal { get; set; } = string.Empty;

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
        NotAvailableNotice = _text.Get("Cleanup_NotAvailable");
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
                Label = _text.Get($"Cleanup_Location_{location.Id}"),
                Path = location.Path,
                IsProtected = !location.IsUserOwned,
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
