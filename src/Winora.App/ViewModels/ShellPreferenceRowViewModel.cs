using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Winora.App.ViewModels;

/// <param name="Text">The stable value identifier, or "unset".</param>
/// <param name="Label">The localized choice shown to the user.</param>
public sealed record ShellPreferenceChoice(string Text, string Label);

/// <summary>
/// One documented Explorer preference. Unlike a visual effect these are not all two-state, and
/// "not set" is a real choice rather than a missing one, so the row offers a list rather than a
/// switch.
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
    public partial string PreviewLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsChangeable { get; set; }

    /// <summary>The value actually read from the registry, or "unset".</summary>
    [ObservableProperty]
    public partial string ObservedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ShellPreferenceChoice? SelectedChoice { get; set; }

    public ObservableCollection<ShellPreferenceChoice> Choices { get; } = [];

    public bool HasBlockReason => !string.IsNullOrEmpty(BlockReason);

    /// <summary>
    /// True only when a real change is pending, which keeps the operation's "already holds the
    /// proposed value" refusal unreachable from the UI.
    /// </summary>
    public bool CanPreview =>
        IsChangeable &&
        SelectedChoice is not null &&
        !string.Equals(SelectedChoice.Text, ObservedText, StringComparison.Ordinal);

    partial void OnBlockReasonChanged(string value) => OnPropertyChanged(nameof(HasBlockReason));

    partial void OnSelectedChoiceChanged(ShellPreferenceChoice? value) => OnPropertyChanged(nameof(CanPreview));

    partial void OnObservedTextChanged(string value) => OnPropertyChanged(nameof(CanPreview));

    partial void OnIsChangeableChanged(bool value) => OnPropertyChanged(nameof(CanPreview));
}
