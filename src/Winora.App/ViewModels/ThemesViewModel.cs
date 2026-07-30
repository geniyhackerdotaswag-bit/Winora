using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Navigation;
using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.App.ViewModels;

/// <summary>
/// The first screen that reaches real Windows state. Probes each documented visual-effect operation,
/// shows its capability honestly, and turns a pending draft into an immutable dry-run plan.
/// </summary>
public sealed partial class ThemesViewModel : ObservableObject
{
    private readonly IReadOnlyList<IOperation> _operations;
    private readonly ILocalizationService _text;
    private readonly INavigationService _navigation;
    private readonly ChangeSessionViewModel _session;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    /// <summary>Set when probing itself failed, so the screen never renders an empty silent list.</summary>
    [ObservableProperty]
    public partial string LoadError { get; set; } = string.Empty;

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasLoadError));

    public ObservableCollection<VisualEffectRowViewModel> Rows { get; } = [];

    public ThemesViewModel(
        IEnumerable<IOperation> operations,
        ILocalizationService text,
        INavigationService navigation,
        ChangeSessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations.ToArray();
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Themes");
        Subtitle = _text.Get("Themes_Subtitle");
        LoadError = string.Empty;
        Rows.Clear();

        foreach (var operation in _operations)
        {
            VisualEffectRowViewModel row;
            try
            {
                row = await BuildRowAsync(operation, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoadError = _text.Get("Themes_ProbeFailed");
                continue;
            }

            Rows.Add(row);
        }
    }

    private async Task<VisualEffectRowViewModel> BuildRowAsync(
        IOperation operation,
        CancellationToken cancellationToken)
    {
        var capability = await operation
            .ProbeAsync(new OperationTarget(operation.OperationId), cancellationToken)
            .ConfigureAwait(true);

        var isChangeable =
            capability.Support is SupportStatus.Supported or SupportStatus.SupportedWithElevation;

        // Null means the state was not readable. The row then shows its reason and stays unchangeable
        // rather than guessing a value and offering an action that cannot succeed.
        var isOn = string.Equals(capability.CurrentValue?.Text, "on", StringComparison.Ordinal);
        if (capability.CurrentValue is null)
        {
            isChangeable = false;
        }

        return new VisualEffectRowViewModel
        {
            OperationId = operation.OperationId,
            Label = _text.Get(LabelKeyFor(operation.OperationId)),
            SupportBadge = _text.Get($"Support_{capability.Support}"),
            BlockReason = capability.BlockReason is null ? string.Empty : _text.Get(capability.BlockReason),
            ObservedValue = isOn,
            DraftValue = isOn,
            IsChangeable = isChangeable,
            PreviewLabel = _text.Get("Action_Preview"),
        };
    }

    [RelayCommand]
    private async Task PreviewAsync(VisualEffectRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanPreview)
        {
            return;
        }

        var operation = _operations.Single(candidate =>
            string.Equals(candidate.OperationId, row.OperationId, StringComparison.Ordinal));

        var draft = new OperationDraft(
            row.OperationId,
            "winora.category.personalization",
            row.Label,
            _text.Get("Themes_PlanSummary"),
            new OperationTarget(row.OperationId),
            new DisplayValue("winora.value.toggle", row.DraftValue ? "on" : "off"));

        var plan = await operation.PreviewAsync(draft, CancellationToken.None).ConfigureAwait(true);
        _session.BeginReview(operation, plan);
        _navigation.NavigateTo(RouteKeys.ChangeReview);
    }

    private static string LabelKeyFor(string operationId) => operationId switch
    {
        "winora.visual-effects.client-area-animation" => "Themes_ClientAreaAnimation",
        "winora.visual-effects.ui-effects" => "Themes_UiEffects",
        _ => operationId,
    };
}
