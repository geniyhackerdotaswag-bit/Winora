using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Navigation;
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
    private readonly INavigationService _navigation;
    private readonly ChangeSessionViewModel _session;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LoadError { get; set; } = string.Empty;

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasLoadError));

    public ObservableCollection<ShellPreferenceRowViewModel> Rows { get; } = [];

    public TaskbarViewModel(
        IEnumerable<IOperation> operations,
        IShellPreferenceCatalog catalog,
        ILocalizationService text,
        INavigationService navigation,
        ChangeSessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations
            .Where(static operation => operation.OperationId.StartsWith(CatalogPrefix, StringComparison.Ordinal))
            .ToArray();
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Taskbar");
        Subtitle = _text.Get("Taskbar_Subtitle");
        LoadError = string.Empty;
        Rows.Clear();

        foreach (var operation in _operations)
        {
            try
            {
                Rows.Add(await BuildRowAsync(operation, cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoadError = _text.Get("Taskbar_ProbeFailed");
            }
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
            Description = _text.Get($"Shell_{resourceStem}_Description"),
            SupportBadge = _text.Get($"Support_{capability.Support}"),
            BlockReason = capability.BlockReason is null ? string.Empty : _text.Get(capability.BlockReason),
            RestartNote = _text.Get("Shell_RestartNote"),
            PreviewLabel = _text.Get("Action_Preview"),
            ObservedText = observed,
            IsChangeable =
                capability.CurrentValue is not null &&
                capability.Support is SupportStatus.Supported or SupportStatus.SupportedWithElevation,
        };

        // "Not set" is offered first because it is the state most of these values start in, and
        // choosing it is how a user returns the registry to the shape Windows shipped.
        row.Choices.Add(new ShellPreferenceChoice("unset", _text.Get("Shell_Value_unset")));
        foreach (var allowed in _catalog.AllowedValuesFor(operation.OperationId))
        {
            var text = allowed.ToString(CultureInfo.InvariantCulture);
            row.Choices.Add(new ShellPreferenceChoice(text, _text.Get($"Shell_{resourceStem}_{text}")));
        }

        row.SelectedChoice = row.Choices.FirstOrDefault(choice =>
            string.Equals(choice.Text, observed, StringComparison.Ordinal));
        return row;
    }

    [RelayCommand]
    private async Task PreviewAsync(ShellPreferenceRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanPreview || row.SelectedChoice is null)
        {
            return;
        }

        var operation = _operations.Single(candidate =>
            string.Equals(candidate.OperationId, row.OperationId, StringComparison.Ordinal));

        var draft = new OperationDraft(
            row.OperationId,
            "winora.category.personalization",
            row.Label,
            _text.Get("Taskbar_PlanSummary"),
            new OperationTarget(row.OperationId),
            new DisplayValue("winora.value.shell-preference", row.SelectedChoice.Text));

        var plan = await operation.PreviewAsync(draft, CancellationToken.None).ConfigureAwait(true);
        _session.BeginReview(operation, plan);
        _navigation.NavigateTo(RouteKeys.ChangeReview);
    }

    private static string ResourceStem(string slug) =>
        string.Concat(slug
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
}
