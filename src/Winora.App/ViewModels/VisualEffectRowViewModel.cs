using CommunityToolkit.Mvvm.ComponentModel;

namespace Winora.App.ViewModels;

/// <summary>
/// One documented toggle on the Themes screen. Holds the observed value and the user's draft
/// separately so a draft that equals the observed value can disable preview instead of producing a
/// plan that changes nothing.
/// </summary>
public sealed partial class VisualEffectRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string OperationId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SupportBadge { get; set; } = string.Empty;

    /// <summary>Localized reason the row cannot be changed, or empty when it can.</summary>
    [ObservableProperty]
    public partial string BlockReason { get; set; } = string.Empty;

    /// <summary>The value actually read from Windows.</summary>
    [ObservableProperty]
    public partial bool ObservedValue { get; set; }

    /// <summary>What the user has selected but not yet previewed.</summary>
    [ObservableProperty]
    public partial bool DraftValue { get; set; }

    /// <summary>False when the capability probe refused direct mutation.</summary>
    [ObservableProperty]
    public partial bool IsChangeable { get; set; }

    [ObservableProperty]
    public partial string PreviewLabel { get; set; } = string.Empty;

    public bool HasBlockReason => !string.IsNullOrEmpty(BlockReason);

    partial void OnBlockReasonChanged(string value) => OnPropertyChanged(nameof(HasBlockReason));

    /// <summary>
    /// True only when a real change is pending. This is what keeps the two documented
    /// InvalidOperationException paths in the operation unreachable from the UI: preview is never
    /// offered for an unreadable target or for a value that already matches.
    /// </summary>
    public bool CanPreview => IsChangeable && DraftValue != ObservedValue;

    partial void OnDraftValueChanged(bool value) => OnPropertyChanged(nameof(CanPreview));

    partial void OnObservedValueChanged(bool value) => OnPropertyChanged(nameof(CanPreview));

    partial void OnIsChangeableChanged(bool value) => OnPropertyChanged(nameof(CanPreview));
}
