using CommunityToolkit.Mvvm.ComponentModel;

namespace Winora.App.ViewModels;

/// <summary>
/// One documented toggle on the Themes screen. The switch is applied as soon as the user moves it,
/// so the row tracks what the system actually holds and puts the switch back if a change did not
/// take — a switch must never rest in a position the machine is not in.
/// </summary>
public sealed partial class VisualEffectRowViewModel : ObservableObject
{
    /// <summary>True while a change is in flight, so the row cannot be re-entered.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string OperationId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    /// <summary>Localized reason the row cannot be changed, or empty when it can.</summary>
    [ObservableProperty]
    public partial string BlockReason { get; set; } = string.Empty;

    /// <summary>The value actually read from Windows.</summary>
    [ObservableProperty]
    public partial bool ObservedValue { get; set; }

    /// <summary>False when the capability probe refused direct mutation.</summary>
    [ObservableProperty]
    public partial bool IsChangeable { get; set; }

    /// <summary>What the switch shows. Bound two-way; the view raises the apply.</summary>
    [ObservableProperty]
    public partial bool SwitchValue { get; set; }

    public bool HasBlockReason => !string.IsNullOrEmpty(BlockReason);

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

    partial void OnBlockReasonChanged(string value) => OnPropertyChanged(nameof(HasBlockReason));
}
