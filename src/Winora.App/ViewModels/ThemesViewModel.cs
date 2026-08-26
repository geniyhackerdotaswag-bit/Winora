using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.App.ViewModels;

/// <summary>
/// The documented visual-effect toggles. Flipping a switch applies the change immediately; the
/// safety pipeline underneath is unchanged, only the confirmation moved to the switch itself.
/// </summary>
public sealed partial class ThemesViewModel : ObservableObject
{
    private readonly IReadOnlyList<IOperation> _operations;
    private readonly IChangeExecutor _executor;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    /// <summary>Set when probing or applying failed, so nothing fails silently.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public ObservableCollection<VisualEffectRowViewModel> Rows { get; } = [];

    /// <summary>
    /// The one line above the list, when some of it is locked.
    /// </summary>
    /// <remarks>
    /// Said once, not on every row. The wording is <c>Cleanup_NeedsAdministrator</c> — the sentence
    /// the cleanup screen already uses — because two phrasings of one fact is one phrasing too
    /// many, and this one is shorter than what the rows were printing.
    /// </remarks>
    [ObservableProperty]
    public partial string ElevationNotice { get; set; } = string.Empty;

    public bool NeedsElevation => !string.IsNullOrEmpty(ElevationNotice);

    partial void OnElevationNoticeChanged(string value) => OnPropertyChanged(nameof(NeedsElevation));

    /// <summary>
    /// The key of the one block an administrator could get past.
    /// </summary>
    /// <remarks>
    /// Deliberately this key alone. A row blocked because the account cannot elevate at all, or
    /// because the value sits on a network share, or because Windows protects it, is not fixed by
    /// restarting with rights — offering the button there would be a promise the restart cannot
    /// keep.
    /// </remarks>
    /// <remarks>
    /// Written in the form the probe actually hands over — dots and hyphens. The underscored
    /// spelling is what the .resw file uses, and only <c>ILocalizationService.Get</c> converts
    /// between the two; comparing against that spelling matched nothing and showed no notice at all
    /// while eleven rows sat locked. <c>ThemesElevationCodeTests</c> pins this string to the
    /// constant the system layer declares, which a view model may not reference itself.
    /// </remarks>
    private const string WritableByAdministrator = "winora.capability.target-not-writable";

    public ThemesViewModel(
        IEnumerable<IOperation> operations,
        IChangeExecutor executor,
        ILocalizationService text)
    {
        ArgumentNullException.ThrowIfNull(operations);

        // This screen owns exactly the documented visual-effect toggles. Filtering by catalog prefix
        // keeps a newly registered domain from silently appearing on someone else's page.
        _operations = operations
            .Where(static operation => operation.OperationId.StartsWith("winora.visual-effects.", StringComparison.Ordinal))
            .ToArray();
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Themes");
        Subtitle = _text.Get("Themes_Subtitle");
        StatusMessage = string.Empty;
        Rows.Clear();

        foreach (var operation in _operations)
        {
            try
            {
                Rows.Add(await BuildRowAsync(operation, cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusMessage = _text.Get("Themes_ProbeFailed");
            }
        }

        ElevationNotice = Rows.Any(row =>
            string.Equals(row.BlockReasonKey, WritableByAdministrator, StringComparison.Ordinal))
                ? _text.Get("Cleanup_NeedsAdministrator")
                : string.Empty;
    }

    /// <summary>
    /// Applies the switch the user just moved. On any outcome other than success the row is put back
    /// to what the system actually holds, so the switch can never show a state that was not applied.
    /// </summary>
    public async Task ToggleAsync(VisualEffectRowViewModel row, bool requested)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsBusy || !row.IsChangeable || requested == row.ObservedValue)
        {
            return;
        }

        row.IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var operation = _operations.Single(candidate =>
                string.Equals(candidate.OperationId, row.OperationId, StringComparison.Ordinal));

            var draft = new OperationDraft(
                row.OperationId,
                "winora.category.personalization",
                row.Label,
                _text.Get("Themes_PlanSummary"),
                new OperationTarget(row.OperationId),
                new DisplayValue("winora.value.toggle", requested ? "on" : "off"));

            var outcome = await _executor.ApplyAsync(operation, draft, CancellationToken.None).ConfigureAwait(true);
            if (!outcome.Succeeded)
            {
                StatusMessage = outcome.Message;
            }

            await RefreshAsync(row, operation).ConfigureAwait(true);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private async Task RefreshAsync(VisualEffectRowViewModel row, IOperation operation)
    {
        var capability = await operation
            .ProbeAsync(new OperationTarget(operation.OperationId), CancellationToken.None)
            .ConfigureAwait(true);

        var isOn = string.Equals(capability.CurrentValue?.Text, "on", StringComparison.Ordinal);
        row.ObservedValue = isOn;
        row.SetSwitchWithoutApplying(isOn);
    }

    private async Task<VisualEffectRowViewModel> BuildRowAsync(
        IOperation operation,
        CancellationToken cancellationToken)
    {
        var capability = await operation
            .ProbeAsync(new OperationTarget(operation.OperationId), cancellationToken)
            .ConfigureAwait(true);

        var isChangeable =
            capability.Support is SupportStatus.Supported or SupportStatus.SupportedWithElevation &&
            capability.CurrentValue is not null;

        var isOn = string.Equals(capability.CurrentValue?.Text, "on", StringComparison.Ordinal);

        var row = new VisualEffectRowViewModel
        {
            OperationId = operation.OperationId,
            Label = _text.Get(LabelKeyFor(operation.OperationId)),
            Detail = _text.Get(DetailKeyFor(operation.OperationId)),
            BlockReason = capability.BlockReason is null ? string.Empty : _text.Get(capability.BlockReason),
            BlockReasonKey = capability.BlockReason ?? string.Empty,
            ObservedValue = isOn,
            IsChangeable = isChangeable,
        };
        row.SetSwitchWithoutApplying(isOn);
        return row;
    }

    /// <summary>The key for the line saying what a setting does.</summary>
    /// <remarks>
    /// Derived from the same id as the label rather than listed beside it, so a setting cannot be
    /// registered with its description quietly left off.
    /// </remarks>
    private static string DetailKeyFor(string operationId) =>
        LabelKeyFor(operationId).Replace("Themes_", "Themes_Detail_", StringComparison.Ordinal);

    /// <summary>
    /// Derives the resource key from the catalog id so a newly registered setting cannot silently
    /// fall back to showing its raw operation id as a label.
    /// </summary>
    private static string LabelKeyFor(string operationId)
    {
        const string prefix = "winora.visual-effects.";
        if (!operationId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return operationId;
        }

        var slug = operationId[prefix.Length..];
        var pascal = string.Concat(slug
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
        return $"Themes_{pascal}";
    }
}
