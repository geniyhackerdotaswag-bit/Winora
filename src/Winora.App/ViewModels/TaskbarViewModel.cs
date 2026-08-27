using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.App.ViewModels;

/// <summary>
/// The documented per-user taskbar and Start preferences. Values Microsoft documents as opaque,
/// undocumented, or disabled are absent from the catalog and therefore cannot appear here at all.
/// </summary>
public sealed partial class TaskbarViewModel : ObservableObject
{
    private const string CatalogPrefix = "winora.shell.";

    private readonly IReadOnlyList<IOperation> _operations;
    private readonly IShellPreferenceCatalog _catalog;
    private readonly ILocalizationService _text;
    private readonly IChangeExecutor _executor;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>Stated once for the page instead of repeated under every row.</summary>
    [ObservableProperty]
    public partial string RestartNote { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public ObservableCollection<ShellPreferenceRowViewModel> Rows { get; } = [];

    public TaskbarViewModel(
        IEnumerable<IOperation> operations,
        IShellPreferenceCatalog catalog,
        ILocalizationService text,
        IChangeExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations
            .Where(static operation => operation.OperationId.StartsWith(CatalogPrefix, StringComparison.Ordinal))
            .ToArray();
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Taskbar");
        Subtitle = _text.Get("Taskbar_Subtitle");
        RestartNote = _text.Get("Shell_RestartNote");
        StatusMessage = string.Empty;

        // Built into a plain list first, then put into Rows in one go, with no await in between.
        //
        // Clearing Rows and then adding with an await between each is the same shape that crashed
        // the animations screen: every await is a point where the page can be navigated away from,
        // and finishing the list afterwards changes a collection WinUI has already torn down. It
        // comes out as "System.Runtime.InteropServices.COMException (0x80004005)" from
        // OnCollectionChanged and closes the whole application, looking from outside like the
        // window shutting by itself.
        var built = new List<ShellPreferenceRowViewModel>(_operations.Count);

        foreach (var operation in _operations)
        {
            try
            {
                built.Add(await BuildRowAsync(operation, cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusMessage = _text.Get("Taskbar_ProbeFailed");
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

        var slug = operation.OperationId[CatalogPrefix.Length..];
        var resourceStem = ResourceStem(slug);
        var observed = capability.CurrentValue?.Text ?? string.Empty;

        var row = new ShellPreferenceRowViewModel
        {
            OperationId = operation.OperationId,
            Label = _text.Get($"Shell_{resourceStem}"),
            BlockReason = capability.BlockReason is null ? string.Empty : _text.Get(capability.BlockReason),
            ObservedText = observed,
            IsChangeable =
                capability.CurrentValue is not null &&
                capability.Support is SupportStatus.Supported or SupportStatus.SupportedWithElevation,
        };

        // Only the documented choices are offered. "Not set" is a registry detail, not something a
        // user thinks about: when the value is absent Windows applies its own default, so the list
        // shows that default as the current selection instead of an empty box.
        foreach (var allowed in _catalog.AllowedValuesFor(operation.OperationId))
        {
            var text = allowed.ToString(CultureInfo.InvariantCulture);
            row.Choices.Add(new ShellPreferenceChoice(text, _text.Get($"Shell_{resourceStem}_{text}")));
        }

        var effective = observed == "unset"
            ? _catalog.DefaultValueFor(operation.OperationId).ToString(CultureInfo.InvariantCulture)
            : observed;

        row.SetChoiceWithoutApplying(row.Choices.FirstOrDefault(choice =>
            string.Equals(choice.Text, effective, StringComparison.Ordinal)));
        return row;
    }

    /// <summary>
    /// Applies the choice the user just made. On any outcome other than success the list is put back
    /// to what the registry actually holds, so a selection can never rest on a value that was not
    /// written.
    /// </summary>
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
                _text.Get("Taskbar_PlanSummary"),
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

            var observed = capability.CurrentValue?.Text ?? string.Empty;
            row.ObservedText = observed;
            var effective = observed == "unset"
                ? _catalog.DefaultValueFor(operation.OperationId).ToString(CultureInfo.InvariantCulture)
                : observed;
            row.SetChoiceWithoutApplying(row.Choices.FirstOrDefault(candidate =>
                string.Equals(candidate.Text, effective, StringComparison.Ordinal)));
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private static string ResourceStem(string slug) =>
        string.Concat(slug
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
}
