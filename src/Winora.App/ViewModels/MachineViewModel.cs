using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>One line: what it is, and what it says.</summary>
public sealed partial class MachineFactViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;
}

/// <summary>One group of lines under a heading.</summary>
public sealed partial class MachineGroupViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    public ObservableCollection<MachineFactViewModel> Facts { get; } = [];
}

/// <summary>
/// What this computer is.
/// </summary>
/// <remarks>
/// The one screen in Winora that changes nothing. There is no plan, no backup and no undo here,
/// because there is nothing to undo — it answers a question and stops. That is worth stating,
/// because every other screen in this program exists to alter something.
/// </remarks>
public sealed partial class MachineViewModel : ObservableObject
{
    private readonly IMachineSummaryService _machine;
    private readonly ILocalizationService _text;

    public MachineViewModel(IMachineSummaryService machine, ILocalizationService text)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CopyLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public ObservableCollection<MachineGroupViewModel> Groups { get; } = [];

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Machine");
        CopyLabel = _text.Get("Machine_Copy");
        StatusMessage = string.Empty;

        // Built whole, then put into the collection in one go. Reading hardware goes through WMI,
        // which is slow enough that a page can be left in the middle of it.
        var built = new List<MachineGroupViewModel>();

        try
        {
            foreach (var group in _machine.Read())
            {
                var model = new MachineGroupViewModel { Title = _text.Get(group.TitleKey) };

                foreach (var fact in group.Facts)
                {
                    model.Facts.Add(new MachineFactViewModel
                    {
                        Label = _text.Get(fact.LabelKey),
                        Value = fact.Value,
                    });
                }

                built.Add(model);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = _text.Get("Machine_ReadFailed");
        }

        cancellationToken.ThrowIfCancellationRequested();

        Groups.Clear();

        foreach (var group in built)
        {
            Groups.Add(group);
        }

        return Task.CompletedTask;
    }

    /// <summary>Says the description is on the clipboard.</summary>
    public void ReportCopied() => StatusMessage = _text.Get("Machine_Copied");

    /// <summary>Says it is not, which is the one thing worth knowing if it went wrong.</summary>
    public void ReportCopyFailed() => StatusMessage = _text.Get("Machine_CopyFailed");

    /// <summary>
    /// Everything on the screen as plain text, for pasting where somebody asked for it.
    /// </summary>
    /// <remarks>
    /// The reason this screen is worth having: "скинь характеристики" is a request people get, and
    /// answering it otherwise means opening four different Windows dialogs and typing it out.
    /// </remarks>
    public string AsText()
    {
        var lines = new List<string>();

        foreach (var group in Groups)
        {
            lines.Add(group.Title);

            foreach (var fact in group.Facts)
            {
                lines.Add($"  {fact.Label}: {fact.Value}");
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines).TrimEnd();
    }
}
