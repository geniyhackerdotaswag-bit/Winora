using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One operation that never finished, and therefore blocks every new change.</summary>
public sealed partial class StuckOperationViewModel : ObservableObject
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
    public partial string RecoverLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool CanRecover => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRecover));
}

/// <summary>
/// The way out when a change did not finish.
/// </summary>
/// <remarks>
/// <para>
/// This screen exists because of where the app sends people. Three of the worst dispositions —
/// <c>PartialRecoveryRequired</c>, <c>Conflict</c> and <c>DurabilityFailure</c> — route here, and
/// until now they landed on a page that said the section was not built. Someone whose change had
/// just gone wrong was told nothing at all.
/// </para>
/// <para>
/// The tone matters as much as the buttons. An unfinished change blocking every new one is the
/// safety design working, not a fault: Winora refuses to start a second change on top of a
/// half-finished one. The screen says that plainly, because a user who thinks the app is broken will
/// go looking for a workaround, and the workaround is worse than the wait.
/// </para>
/// </remarks>
public sealed partial class RecoveryViewModel : ObservableObject
{
    private readonly IChangeHistoryService _history;
    private readonly IRecoveryState _recovery;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    /// <summary>States that the block is protection rather than a fault.</summary>
    [ObservableProperty]
    public partial string ExplanationTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Explanation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecoverAllLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RefreshLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ClearMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>True when nothing is stuck, which is the state a user should normally be in.</summary>
    [ObservableProperty]
    public partial bool IsClear { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool CanRecoverAll => !IsClear && !IsBusy;

    partial void OnIsClearChanged(bool value) => OnPropertyChanged(nameof(CanRecoverAll));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRecoverAll));

    public ObservableCollection<StuckOperationViewModel> Stuck { get; } = [];

    public RecoveryViewModel(
        IChangeHistoryService history,
        IRecoveryState recovery,
        ILocalizationService text)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Recovery");
        Subtitle = _text.Get("Recovery_Subtitle");
        ExplanationTitle = _text.Get("Recovery_Explanation_Title");
        Explanation = _text.Get("Recovery_Explanation");
        RecoverAllLabel = _text.Get("Recovery_RecoverAll");
        RefreshLabel = _text.Get("Recovery_Refresh");
        ClearMessage = _text.Get("Recovery_Clear");

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Stuck.Clear();

        IReadOnlyList<ChangeRecordView> records;
        try
        {
            records = await _history.ReadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = _text.Get("Recovery_JournalUnreadable");
            IsClear = false;
            return;
        }

        foreach (var record in records.Where(static record => !record.IsComplete))
        {
            Stuck.Add(new StuckOperationViewModel
            {
                OperationId = record.OperationId,
                Title = record.Title.Length > 0 ? record.Title : _text.Get("Changes_UnknownTitle"),
                Summary = record.Summary,
                Outcome = _text.Get(record.OutcomeResourceKey),
                When = record.OccurredAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                RecoverLabel = _text.Get("Recovery_RecoverOne"),
            });
        }

        IsClear = Stuck.Count == 0;
    }

    /// <summary>Reconciles every stuck operation, which is what unblocks the app.</summary>
    public async Task RecoverAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var outcome = await _recovery.RecoverAsync().ConfigureAwait(true);

            StatusMessage = outcome.Failed == 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    _text.Get("Recovery_AllDone"),
                    outcome.Recovered)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    _text.Get("Recovery_PartlyDone"),
                    outcome.Recovered,
                    outcome.Failed,
                    outcome.FirstFailure);

            await ReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RecoverAsync(StuckOperationViewModel operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.IsBusy)
        {
            return;
        }

        operation.IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var attempt = await _history.RollBackAsync(operation.OperationId).ConfigureAwait(true);

            StatusMessage = attempt.Succeeded
                ? _text.Get("Recovery_OneDone")
                : _text.Get(attempt.FailureResourceKey);

            await ReloadAsync().ConfigureAwait(true);
        }
        finally
        {
            operation.IsBusy = false;
        }
    }
}
