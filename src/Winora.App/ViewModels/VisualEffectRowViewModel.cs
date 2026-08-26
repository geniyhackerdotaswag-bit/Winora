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

    /// <summary>
    /// What the setting does, in one line.
    /// </summary>
    /// <remarks>
    /// New on 2026-08-26, and the row had nowhere to put it before: the slot under the name was
    /// occupied by <see cref="BlockReason"/>, which is a diagnostic reason code from the core
    /// printed verbatim — eight identical copies of "Настройку видно, но записать её этой учётной
    /// записи нельзя" down one screen, where a description of the setting belonged.
    /// </remarks>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>
    /// The reason's key, unresolved.
    /// </summary>
    /// <remarks>
    /// The screen needs to tell one kind of block from another — a setting an administrator could
    /// write is a different situation from one this account can never write — and the translated
    /// sentence is the wrong thing to branch on. Eleven reasons exist and they mean different
    /// things; none is collapsed into a general phrase.
    /// </remarks>
    [ObservableProperty]
    public partial string BlockReasonKey { get; set; } = string.Empty;

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
