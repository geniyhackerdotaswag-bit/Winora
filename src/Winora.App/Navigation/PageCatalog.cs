using Winora.App.Views;

namespace Winora.App.Navigation;

/// <summary>
/// The only file that names concrete page types. Keeping <c>typeof(Page)</c> out of
/// <see cref="RouteRegistry"/> is what lets the registry be validated without loading WinUI.
/// </summary>
public static class PageCatalog
{
    /// <summary>
    /// Resolves the page type for a route. Screens that have no dedicated page yet resolve to the
    /// shared placeholder, which states plainly that the section is not built — never a blank frame.
    /// </summary>
    public static Type PageTypeFor(string routeKey) => routeKey switch
    {
        _ => typeof(PlaceholderPage),
    };
}
