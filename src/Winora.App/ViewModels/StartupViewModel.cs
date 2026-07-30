using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Navigation;
using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;

namespace Winora.App.ViewModels;

/// <summary>One documented Run entry as shown on the Startup screen.</summary>
public sealed partial class StartupEntryRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Command { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceBadge { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OperationId { get; set; } = string.Empty;

    /// <summary>The state actually read from the registry.</summary>
    [ObservableProperty]
    public partial bool ObservedEnabled { get; set; }

    [ObservableProperty]
    public partial bool DraftEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsChangeable { get; set; }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>True only when a real change is pending, so a no-op plan is never offered.</summary>
    public bool CanPreview => IsChangeable && DraftEnabled != ObservedEnabled;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    partial void OnDraftEnabledChanged(bool value) => OnPropertyChanged(nameof(CanPreview));

    partial void OnObservedEnabledChanged(bool value) => OnPropertyChanged(nameof(CanPreview));

    partial void OnIsChangeableChanged(bool value) => OnPropertyChanged(nameof(CanPreview));
}

/// <summary>
/// Shows what launches at sign-in and lets per-user entries be turned off. Disabling moves the value
/// into a Winora-owned key rather than deleting it, so the entry can always be restored.
/// </summary>
public sealed partial class StartupViewModel : ObservableObject
{
    private readonly IStartupInventoryService _inventory;
    private readonly IOperationCatalog _catalog;
    private readonly ILocalizationService _text;
    private readonly INavigationService _navigation;
    private readonly ChangeSessionViewModel _session;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CountSummary { get; set; } = string.Empty;

    public ObservableCollection<StartupEntryRowViewModel> Rows { get; } = [];

    public StartupViewModel(
        IStartupInventoryService inventory,
        IOperationCatalog catalog,
        ILocalizationService text,
        INavigationService navigation,
        ChangeSessionViewModel session)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Startup");
        Subtitle = _text.Get("Startup_Subtitle");
        Rows.Clear();

        var entries = _inventory.Read();
        foreach (var entry in entries)
        {
            Rows.Add(new StartupEntryRowViewModel
            {
                Name = entry.Name,
                Command = entry.Command,
                OperationId = entry.OperationId ?? string.Empty,
                ObservedEnabled = entry.IsEnabled,
                DraftEnabled = entry.IsEnabled,
                IsChangeable = entry.OperationId is not null && entry.IsDocumentedKind,
                PreviewLabel = _text.Get("Action_Preview"),
                SourceBadge = _text.Get(entry.IsMachineWide ? "Startup_Source_Machine" : "Startup_Source_User"),
                Note = !entry.IsDocumentedKind
                    ? _text.Get("Startup_UndocumentedKind")
                    : entry.IsMachineWide
                        ? _text.Get("Startup_MachineReadOnly")
                        : entry.IsEnabled
                            ? string.Empty
                            : _text.Get("Startup_HeldByWinora"),
            });
        }

        CountSummary = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Startup_CountFormat"),
            entries.Count(static e => !e.IsMachineWide),
            entries.Count(static e => e.IsMachineWide));

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task PreviewAsync(StartupEntryRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanPreview)
        {
            return;
        }

        // Resolved through the catalog because startup operations are built per discovered entry
        // rather than registered up front.
        var operation = _catalog.Resolve(row.OperationId);

        var draft = new OperationDraft(
            row.OperationId,
            "winora.category.system",
            row.Name,
            _text.Get("Startup_PlanSummary"),
            new OperationTarget(row.OperationId),
            new DisplayValue("winora.value.startup-state", row.DraftEnabled ? "enabled" : "disabled"));

        var plan = await operation.PreviewAsync(draft, CancellationToken.None).ConfigureAwait(true);
        _session.BeginReview(operation, plan);
        _navigation.NavigateTo(RouteKeys.ChangeReview);
    }
}
