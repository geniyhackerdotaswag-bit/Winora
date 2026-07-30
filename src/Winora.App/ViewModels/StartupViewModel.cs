using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

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

    public bool HasNote => !string.IsNullOrEmpty(Note);

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
}

/// <summary>
/// Shows what launches at sign-in and from where. Inspection only: enabling and disabling are not
/// implemented yet, and the screen says so rather than offering a control that does nothing.
/// </summary>
public sealed partial class StartupViewModel : ObservableObject
{
    private readonly IStartupInventoryService _inventory;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NotAvailableNotice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CountSummary { get; set; } = string.Empty;

    public ObservableCollection<StartupEntryRowViewModel> Rows { get; } = [];

    public StartupViewModel(IStartupInventoryService inventory, ILocalizationService text)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Title = _text.Get("Nav_Startup");
        Subtitle = _text.Get("Startup_Subtitle");
        NotAvailableNotice = _text.Get("Startup_NotAvailable");
        Rows.Clear();

        var entries = _inventory.Read();
        foreach (var entry in entries)
        {
            Rows.Add(new StartupEntryRowViewModel
            {
                Name = entry.Name,
                Command = entry.Command,
                SourceBadge = _text.Get(entry.IsMachineWide ? "Startup_Source_Machine" : "Startup_Source_User"),
                Note = entry.IsDocumentedKind ? string.Empty : _text.Get("Startup_UndocumentedKind"),
            });
        }

        CountSummary = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Startup_CountFormat"),
            entries.Count(static e => !e.IsMachineWide),
            entries.Count(static e => e.IsMachineWide));

        return Task.CompletedTask;
    }
}
