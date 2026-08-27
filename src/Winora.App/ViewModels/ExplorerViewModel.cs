using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.App.ViewModels;

/// <summary>
/// What File Explorer shows: file extensions, and hidden files.
/// </summary>
/// <remarks>
/// <para>
/// Two settings, and the first is why the screen exists. Windows hides the extension of a known
/// file type by default, which is how something named <c>photo.jpg.exe</c> arrives looking like a
/// photograph: the icon is whatever its author chose, and the part that would give it away is the
/// part Windows removes.
/// </para>
/// <para>
/// The rows are the same documented per-user Explorer values the taskbar screen changes, through
/// the same operation, plan, verified write and undo. Nothing new happens here — these two values
/// simply had no screen. The two screens tell their rows apart by the middle of the operation
/// identifier, so a value belonging to one can never appear on the other.
/// </para>
/// </remarks>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private const string CatalogPrefix = "winora.explorer.";

    private readonly IReadOnlyList<IOperation> _operations;
    private readonly IShellPreferenceCatalog _catalog;
    private readonly IChangeExecutor _executor;
    private readonly ILocalizationService _text;

    public ExplorerViewModel(
        IEnumerable<IOperation> operations,
        IShellPreferenceCatalog catalog,
        IChangeExecutor executor,
        ILocalizationService text)
    {
        ArgumentNullException.ThrowIfNull(operations);

        _operations = operations
            .Where(static operation => operation.OperationId.StartsWith(CatalogPrefix, StringComparison.Ordinal))
            .ToArray();

        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>Why this screen is worth opening, said once above the rows.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RestartNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public ObservableCollection<ShellPreferenceRowViewModel> Rows { get; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Explorer");
        Subtitle = _text.Get("Explorer_Subtitle");
        RestartNote = _text.Get("Shell_RestartNote");
        StatusMessage = string.Empty;

        // Built into a plain list first, then put into Rows in one go, with no await in between.
        // Clearing and adding across awaits is what crashed the animations screen: a page can be
        // left at any await, and finishing the list afterwards changes a collection WinUI has
        // already torn down.
        var built = new List<ShellPreferenceRowViewModel>(_operations.Count);

        foreach (var operation in _operations)
        {
            try
            {
                built.Add(await BuildRowAsync(operation, cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusMessage = _text.Get("Explorer_ProbeFailed");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        Rows.Clear();

        foreach (var row in built)
        {
            Rows.Add(row);
        }
    }

    private async Task<ShellPreferenceRowViewModel> BuildRowAsync(
        IOperation operation,
        CancellationToken cancellationToken)
    {
        var capability = await operation
            .ProbeAsync(new OperationTarget(operation.OperationId), cancellationToken)
            .ConfigureAwait(true);

        var stem = ResourceStem(operation.OperationId[CatalogPrefix.Length..]);
        var observed = capability.CurrentValue?.Text ?? string.Empty;

        var row = new ShellPreferenceRowViewModel
        {
            OperationId = operation.OperationId,
            Label = _text.Get($"Explorer_{stem}"),
            BlockReason = capability.BlockReason is null ? string.Empty : _text.Get(capability.BlockReason),
            ObservedText = observed,
            IsChangeable =
                capability.CurrentValue is not null &&
                capability.Support is SupportStatus.Supported or SupportStatus.SupportedWithElevation,
        };

        foreach (var allowed in _catalog.AllowedValuesFor(operation.OperationId))
        {
            var text = allowed.ToString(CultureInfo.InvariantCulture);
            row.Choices.Add(new ShellPreferenceChoice(text, _text.Get($"Explorer_{stem}_{text}")));
        }

        row.SetChoiceWithoutApplying(ChoiceFor(row, operation, observed));
        return row;
    }

    /// <summary>
    /// Applies the choice just made, then puts the list back to whatever Windows actually holds.
    /// </summary>
    /// <remarks>
    /// Re-read rather than trusted. A refused or drifted write is exactly the case where the value
    /// sent and the value held differ, and a list resting on a value that was never written is the
    /// one thing this screen must not show.
    /// </remarks>
    public async Task SelectAsync(ShellPreferenceRowViewModel row, ShellPreferenceChoice? choice)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsBusy || !row.IsChangeable || choice is null ||
            string.Equals(choice.Text, row.ObservedText, StringComparison.Ordinal))
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
                _text.Get("Explorer_PlanSummary"),
                new OperationTarget(row.OperationId),
                new DisplayValue("winora.value.shell-preference", choice.Text));

            var outcome = await _executor.ApplyAsync(operation, draft, CancellationToken.None).ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                StatusMessage = outcome.Message;
            }

            var capability = await operation
                .ProbeAsync(new OperationTarget(operation.OperationId), CancellationToken.None)
                .ConfigureAwait(true);

            row.ObservedText = capability.CurrentValue?.Text ?? string.Empty;
            row.SetChoiceWithoutApplying(ChoiceFor(row, operation, row.ObservedText));
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>
    /// The choice matching what Windows holds, with absence resolved to the default it applies.
    /// </summary>
    /// <remarks>
    /// "Not set" is a registry detail rather than something anybody thinks about: when the value is
    /// absent Windows applies its own default, so the list shows that default as current instead of
    /// an empty box.
    /// </remarks>
    private ShellPreferenceChoice? ChoiceFor(
        ShellPreferenceRowViewModel row,
        IOperation operation,
        string observed)
    {
        var effective = observed == "unset"
            ? _catalog.DefaultValueFor(operation.OperationId).ToString(CultureInfo.InvariantCulture)
            : observed;

        return row.Choices.FirstOrDefault(choice =>
            string.Equals(choice.Text, effective, StringComparison.Ordinal));
    }

    private static string ResourceStem(string slug) =>
        string.Concat(slug
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
}
