using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
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
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OperationId { get; set; } = string.Empty;

    /// <summary>The state actually read from the registry.</summary>
    [ObservableProperty]
    public partial bool ObservedEnabled { get; set; }

    /// <summary>What the switch shows. Bound two-way; the view raises the apply.</summary>
    [ObservableProperty]
    public partial bool SwitchValue { get; set; }

    [ObservableProperty]
    public partial bool IsChangeable { get; set; }

    /// <summary>True while a change is in flight, so the row cannot be re-entered.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>
    /// True while the switch is being moved by code rather than by the user, so the view can ignore
    /// the resulting event instead of applying a change nobody asked for.
    /// </summary>
    public bool IsSettingProgrammatically { get; private set; }

    /// <summary>Moves the switch to match reality without triggering an apply.</summary>
    public void SetSwitchWithoutApplying(bool value)
    {
        IsSettingProgrammatically = true;
        try
        {
            SwitchValue = value;
        }
        finally
        {
            IsSettingProgrammatically = false;
        }
    }

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
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
    private readonly IChangeExecutor _executor;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CountSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public ObservableCollection<StartupEntryRowViewModel> Rows { get; } = [];

    public StartupViewModel(
        IStartupInventoryService inventory,
        IOperationCatalog catalog,
        ILocalizationService text,
        IChangeExecutor executor)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
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
            var row = new StartupEntryRowViewModel
            {
                Name = entry.Name,
                Command = entry.Command,
                OperationId = entry.OperationId ?? string.Empty,
                ObservedEnabled = entry.IsEnabled,
                IsChangeable = entry.OperationId is not null && entry.IsDocumentedKind,
                // A note only when the switch cannot be used. "This one is off" needs no caption:
                // the switch already says so, and repeating it on every row was just noise.
                Note = !entry.IsDocumentedKind
                    ? _text.Get("Startup_UndocumentedKind")
                    : entry.IsMachineWide
                        ? _text.Get("Startup_MachineReadOnly")
                        : string.Empty,
            };
            row.SetSwitchWithoutApplying(entry.IsEnabled);
            Rows.Add(row);
        }

        CountSummary = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Startup_CountFormat"),
            entries.Count(static e => !e.IsMachineWide),
            entries.Count(static e => e.IsMachineWide));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies the switch the user just moved. Disabling moves the value into a Winora-owned key
    /// rather than deleting it, so the entry can always be turned back on.
    /// </summary>
    public async Task ToggleAsync(StartupEntryRowViewModel row, bool requested)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsBusy || !row.IsChangeable || requested == row.ObservedEnabled)
        {
            return;
        }

        row.IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            // Resolved through the catalog because startup operations are built per discovered entry
            // rather than registered up front.
            var operation = _catalog.Resolve(row.OperationId);

            var draft = new OperationDraft(
                row.OperationId,
                "winora.category.system",
                row.Name,
                _text.Get("Startup_PlanSummary"),
                new OperationTarget(row.OperationId),
                new DisplayValue("winora.value.startup-state", requested ? "enabled" : "disabled"));

            var outcome = await _executor.ApplyAsync(operation, draft, CancellationToken.None).ConfigureAwait(true);
            if (!outcome.Succeeded)
            {
                StatusMessage = outcome.Message;
            }

            // Re-read the inventory so the switch reflects the registry, not the request.
            var actual = _inventory.Read().FirstOrDefault(entry =>
                string.Equals(entry.OperationId, row.OperationId, StringComparison.Ordinal));
            if (actual is not null)
            {
                row.ObservedEnabled = actual.IsEnabled;
                row.SetSwitchWithoutApplying(actual.IsEnabled);
            }
        }
        finally
        {
            row.IsBusy = false;
        }
    }
}
