using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Winora.App.ViewModels;

/// <param name="Text">The stable value identifier.</param>
/// <param name="Label">The localized choice shown to the user.</param>
public sealed record ShellPreferenceChoice(string Text, string Label);

/// <summary>
/// One documented Explorer preference. These are not all two-state, so the row offers a list rather
/// than a switch. Choosing applies immediately and the list is put back to reality if it did not.
/// </summary>
public sealed partial class ShellPreferenceRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string OperationId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SupportBadge { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BlockReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RestartNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsChangeable { get; set; }

    /// <summary>True while a change is in flight, so the row cannot be re-entered.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The value actually read from the registry, or "unset" when absent.</summary>
    [ObservableProperty]
    public partial string ObservedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ShellPreferenceChoice? SelectedChoice { get; set; }

    public ObservableCollection<ShellPreferenceChoice> Choices { get; } = [];

    public bool HasBlockReason => !string.IsNullOrEmpty(BlockReason);

    /// <summary>
    /// True while the selection is being moved by code rather than by the user, so the view can
    /// ignore the resulting event instead of applying a change nobody asked for.
    /// </summary>
    public bool IsSettingProgrammatically { get; private set; }

    /// <summary>Moves the selection to match reality without triggering an apply.</summary>
    public void SetChoiceWithoutApplying(ShellPreferenceChoice? choice)
    {
        IsSettingProgrammatically = true;
        try
        {
            SelectedChoice = choice;
        }
        finally
        {
            IsSettingProgrammatically = false;
        }
    }

    partial void OnBlockReasonChanged(string value) => OnPropertyChanged(nameof(HasBlockReason));
}
