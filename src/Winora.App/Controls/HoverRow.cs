using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Winora.App.Controls;

/// <summary>
/// A row on a grouped surface that lights up under the pointer.
/// <para>
/// A plain <see cref="ContentControl"/> has no pointer visual states, so a list built from one sits
/// there inert no matter where the mouse is. This adds the two states and nothing else: the styling
/// itself lives in <c>Controls.xaml</c> so every screen highlights identically, and the brush comes
/// from the native subtle-fill theme resource so High Contrast overrides it correctly.
/// </para>
/// </summary>
public sealed partial class HoverRow : ContentControl
{
    private const string NormalState = "Normal";
    private const string PointerOverState = "PointerOver";

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        VisualStateManager.GoToState(this, PointerOverState, useTransitions: true);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        VisualStateManager.GoToState(this, NormalState, useTransitions: true);
    }

    /// <summary>A pointer lost while a control inside the row captured it must not leave it lit.</summary>
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        VisualStateManager.GoToState(this, NormalState, useTransitions: true);
    }
}
