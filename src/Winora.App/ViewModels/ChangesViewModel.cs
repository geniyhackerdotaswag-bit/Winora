using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One change Winora made, as a row.</summary>
public sealed partial class ChangeRecordViewModel : ObservableObject
{
    public Guid OperationId { get; init; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    public bool HasSummary => !string.IsNullOrEmpty(Summary);

    [ObservableProperty]
    public partial string Outcome { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string When { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RollBackLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsReversible { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool CanRollBack => IsReversible && !IsBusy;

    partial void OnIsReversibleChanged(bool value) => OnPropertyChanged(nameof(CanRollBack));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRollBack));
}

/// <summary>
/// Everything Winora has changed, and the way to undo any of it.
/// </summary>
/// <remarks>
/// <para>
/// This screen is where the app's central promise becomes checkable. Until it existed a user could
/// undo a change only from the result page they happened to be looking at, or through the recovery
/// button, which only handles operations that never finished — a change that completed successfully
/// had no route back at all.
/// </para>
/// <para>
/// A row offers rollback only when everything needed is already on disk. An operation whose plan or
/// backup is gone is still listed, so nothing the app did is hidden, but it says plainly that it
/// cannot be undone rather than offering a button that would fail.
/// </para>
/// </remarks>
public sealed partial class ChangesViewModel : ObservableObject
{
    private readonly IChangeHistoryService _history;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RefreshLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = string.Empty;

    public ObservableCollection<ChangeRecordViewModel> Records { get; } = [];

    public ChangesViewModel(IChangeHistoryService history, ILocalizationService text)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Changes");
        Subtitle = _text.Get("Changes_Subtitle");
        RefreshLabel = _text.Get("Changes_Refresh");
        EmptyMessage = _text.Get("Changes_Empty");

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = string.Empty;
        Records.Clear();

        IReadOnlyList<ChangeRecordView> records;
        try
        {
            records = await _history.ReadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A history Winora cannot read is worth saying out loud: it also means the journal the
            // safety machinery depends on is unreadable.
            StatusMessage = _text.Get("Changes_Unreadable");
            IsEmpty = false;
            return;
        }

        foreach (var record in records)
        {
            Records.Add(new ChangeRecordViewModel
            {
                OperationId = record.OperationId,
                Title = record.Title.Length > 0 ? record.Title : _text.Get("Changes_UnknownTitle"),
                Summary = record.Summary,
                Outcome = _text.Get(record.OutcomeResourceKey),
                When = record.OccurredAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                RollBackLabel = _text.Get("Changes_RollBack"),
                IsReversible = record.IsReversible,
            });
        }

        IsEmpty = Records.Count == 0;
    }

    public async Task RollBackAsync(ChangeRecordViewModel record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.IsBusy || !record.IsReversible)
        {
            return;
        }

        record.IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var attempt = await _history.RollBackAsync(record.OperationId).ConfigureAwait(true);

            StatusMessage = attempt.Succeeded
                ? _text.Get("Changes_RolledBack")
                : _text.Get(attempt.FailureResourceKey);

            // Re-read rather than patch the row: the rollback is itself a recorded operation, so the
            // list after it is genuinely different from the list before.
            await ReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            record.IsBusy = false;
        }
    }
}
