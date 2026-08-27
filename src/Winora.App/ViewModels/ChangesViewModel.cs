using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;
using Winora.Core.Changes;

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
                Title = record.Title.Length > 0
                    ? ChangeCaption.Readable(record.Title)
                    : _text.Get("Changes_UnknownTitle"),
                Summary = Was(record.Summary),
                Outcome = _text.Get(record.OutcomeResourceKey),
                When = record.OccurredAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                RollBackLabel = _text.Get("Changes_RollBack"),
                IsReversible = record.IsReversible,
            });
        }

        IsEmpty = Records.Count == 0;
    }

    /// <summary>
    /// The state a change moved away from, said as a fact rather than as a pair of tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The journal stores "enabled → disabled". Both halves are on the screen already: the row's
    /// name says what was changed and the outcome says it was applied, so printing the arrow makes
    /// a person work out the direction from two words in a language the program does not otherwise
    /// speak. What is missing, and what "undo" actually needs, is what it will go back to.
    /// </para>
    /// <para>
    /// Only the vocabulary this program writes is translated. A value it did not write — a number,
    /// a path, a name — is shown as stored, because that value is the truth and a paraphrase of it
    /// would be a guess on the one screen that must not guess.
    /// </para>
    /// </remarks>
    private string Was(string summary)
    {
        var before = ChangeCaption.Before(summary);

        if (before.Length == 0)
        {
            return string.Empty;
        }

        var known = before switch
        {
            "enabled" => "Changes_Value_Enabled",
            "disabled" => "Changes_Value_Disabled",
            "on" => "Changes_Value_On",
            "off" => "Changes_Value_Off",
            "unset" => "Changes_Value_Unset",
            _ => null,
        };

        var readable = known is not null
            ? _text.Get(known)
            : Appearance(before) ?? before;

        return string.Format(CultureInfo.CurrentCulture, _text.Get("Changes_Was"), readable);
    }

    /// <summary>
    /// A Windows appearance in words, or null when the value is not one.
    /// </summary>
    /// <remarks>
    /// This vocabulary is Winora's own, and it went to screen raw: the history read
    /// <c>было: dark auto</c>, which is two English words of machine shorthand on the one screen
    /// that exists to be believed. The colour keeps its hex, because a colour named in prose is a
    /// guess and the number is the truth.
    /// </remarks>
    private string? Appearance(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var mode = parts.Length > 0
            ? parts[0] switch
            {
                "dark" => _text.Get("Changes_Value_ThemeDark"),
                "light" => _text.Get("Changes_Value_ThemeLight"),
                _ => null,
            }
            : null;

        if (mode is null || parts.Length > 3)
        {
            return null;
        }

        if (parts.Length == 1)
        {
            return mode;
        }

        // Written by an earlier build only. Kept so those rows read as words rather than as the
        // program's own shorthand on the one screen that has to be believed.
        if (string.Equals(parts[1], "auto", StringComparison.Ordinal))
        {
            return string.Format(CultureInfo.CurrentCulture, _text.Get("Changes_Value_ThemeAuto"), mode);
        }

        return parts.Length == 2
            ? string.Format(
                CultureInfo.CurrentCulture,
                _text.Get("Changes_Value_ThemeColour"),
                mode,
                parts[1].ToUpperInvariant())
            : null;
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
