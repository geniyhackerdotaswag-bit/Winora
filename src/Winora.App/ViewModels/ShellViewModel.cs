using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Navigation;

namespace Winora.App.ViewModels;

/// <summary>
/// Backs the navigation shell. Owns the pane structure and the selected route key, and knows nothing
/// about concrete page types — those stay in the page catalog so this stays testable.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly RouteRegistry _routes;

    /// <remarks>
    /// A partial property, not a field: MVVMTK0045 requires this form in WinUI 3 so the CsWinRT
    /// generators can emit the WinRT marshalling code.
    /// </remarks>
    [ObservableProperty]
    public partial string SelectedRouteKey { get; set; }

    public ShellViewModel(RouteRegistry routes)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        SelectedRouteKey = _routes.StartRouteKey;
    }

    /// <summary>Top-level items shown above the first group heading.</summary>
    public IReadOnlyList<RouteDescriptor> RootItems { get; private set; } = [];

    /// <summary>Grouped pane items, in registration order, keyed by group resource key.</summary>
    public IReadOnlyList<IGrouping<string, RouteDescriptor>> Groups { get; private set; } = [];

    /// <summary>Pane footer items.</summary>
    public IReadOnlyList<RouteDescriptor> FooterItems { get; private set; } = [];

    public void Load()
    {
        RootItems = _routes.Routes
            .Where(static route => route.Placement == RoutePlacement.PaneRoot)
            .ToArray();

        Groups = _routes.Routes
            .Where(static route => route.Placement == RoutePlacement.Pane)
            .GroupBy(static route => route.GroupResourceKey!)
            .ToArray();

        FooterItems = _routes.Routes
            .Where(static route => route.Placement == RoutePlacement.Footer)
            .ToArray();
    }
}
