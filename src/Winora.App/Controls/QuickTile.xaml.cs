using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winora.App.Services;
using Winora.App.ViewModels;

namespace Winora.App.Controls;

/// <summary>One of the dashboard's quick actions.</summary>
/// <remarks>
/// A control rather than a <c>DataTemplate</c> with bindings, for the same reason
/// <see cref="ProfileCard"/> is one: what the tile is given are two keys, and turning a resource
/// key into a sentence and a catalog key into an icon is not something markup can do. Bound
/// directly, the screen would have shown the keys — which happened once, and a person read
/// "[winora.cleanup.windows-serviced]" off the middle of a page.
/// </remarks>
public sealed partial class QuickTile : UserControl
{
    public QuickTile() => InitializeComponent();

    /// <summary>Where this tile leads. Empty until <see cref="Show"/> has run.</summary>
    public string RouteKey { get; private set; } = string.Empty;

    /// <summary>Raised when the tile is pressed, however it was pressed.</summary>
    public event EventHandler<string>? Activated;

    /// <summary>Fills the tile in from one quick action.</summary>
    public void Show(QuickAction action, ILocalizationService text)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(text);

        RouteKey = action.RouteKey;

        var name = text.Get(action.Title);
        TileName.Text = name;
        TileDetail.Text = text.Get(action.Description);
        Mark.Content = CatalogIcon.Create(action.IconGlyphKey);

        // The icon is decorative here — it repeats the name beside it — so the tile is announced by
        // its name, and a screen reader is not made to read the mark twice.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Surface, name);
    }

    private void OnClicked(object sender, RoutedEventArgs args) => Activated?.Invoke(this, RouteKey);
}
