using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Winora.App.Controls;

/// <summary>
/// Small helpers for <c>x:Bind</c> function binding, so ViewModels can expose plain booleans instead
/// of WinUI <see cref="Visibility"/> values.
/// </summary>
public static class XamlConvert
{
    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>For the common case of "enabled while not busy".</summary>
    public static bool Not(bool value) => !value;

    /// <summary>
    /// Shows a message only when there is one.
    /// </summary>
    /// <remarks>
    /// A message bound to an empty string is not invisible: the block keeps its line height, and in
    /// a spaced stack that is a gap which opens and closes as somebody types. The registration
    /// wizard has four of these — two field errors, the repeated password and the save failure —
    /// and every one of them is empty for most of the time it is on screen.
    /// </remarks>
    public static Visibility ShowText(string? value) =>
        string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The inverse of <see cref="Show"/>. Needed because x:Bind cannot nest function bindings, and a
    /// control that must disappear while its busy indicator is up has no other way to say so.
    /// </summary>
    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The accent bar on a setting tile: full strength when the setting is on, a faint trace when
    /// off. It stays visible either way so the tile keeps its shape, and it never carries meaning
    /// the switch beside it does not already state.
    /// </summary>
    public static double BarOpacity(bool isOn) => isOn ? 1.0 : 0.18;

    /// <summary>Informational when the condition holds, a warning when it does not.</summary>
    public static InfoBarSeverity InfoSeverity(bool isHealthy) =>
        isHealthy ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;

    /// <summary>Green only when nothing failed. A partial run must not wear a success tick.</summary>
    public static InfoBarSeverity OutcomeSeverity(bool anyFailed) =>
        anyFailed ? InfoBarSeverity.Warning : InfoBarSeverity.Success;

    /// <summary>
    /// A swatch fill for a colour the user is choosing.
    /// </summary>
    /// <remarks>
    /// These are the only brushes in the app built from a value rather than taken from the palette,
    /// because a swatch showing a candidate colour is the one case where the colour is the content.
    /// A fresh brush per call is deliberate: these are bound per item and must not share an instance
    /// with the palette, which <c>ThemeBrushService</c> repaints in place.
    /// </remarks>
    public static Brush Swatch(Color colour) => new SolidColorBrush(colour);

    /// <summary>
    /// The severity of one contrast result: a failure is a warning, a pass is not decorated.
    /// </summary>
    public static InfoBarSeverity CheckSeverity(bool passes) =>
        passes ? InfoBarSeverity.Success : InfoBarSeverity.Error;

    /// <summary>
    /// The update strip's severity: error for something the person has to act on, informational for
    /// routine status. Review finding (Minor 3): the strip used to be a static "Informational" in
    /// XAML no matter what it said, including for a failure that had moved the program's own
    /// executable aside.
    /// </summary>
    public static InfoBarSeverity UpdateSeverity(bool isFailure) =>
        isFailure ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
}
