namespace Winora.App.Navigation;

/// <summary>Where a route appears in the shell, if anywhere.</summary>
public enum RoutePlacement
{
    /// <summary>A top-level pane item with no group heading above it.</summary>
    PaneRoot,

    /// <summary>A pane item inside a titled group.</summary>
    Pane,

    /// <summary>A pane footer item.</summary>
    Footer,

    /// <summary>Reachable only by navigation, never offered as a pane item.</summary>
    RouteOnly,
}

/// <summary>
/// One navigable screen. Deliberately holds no <c>Type</c> for a page: keeping this a plain record
/// lets the registry be validated without loading WinUI. Page types live in the page catalog.
/// </summary>
/// <param name="Key">Stable, lowercase, kebab-cased identifier.</param>
/// <param name="TitleResourceKey">`.resw` key for the screen heading.</param>
/// <param name="Placement">Where the shell offers this route.</param>
/// <param name="GroupResourceKey">`.resw` key for the group heading; required for <see cref="RoutePlacement.Pane"/>.</param>
/// <param name="IconGlyphKey">Catalog key for the shared 20 px icon presenter; absent for route-only screens.</param>
public sealed record RouteDescriptor(
    string Key,
    string TitleResourceKey,
    RoutePlacement Placement,
    string? GroupResourceKey = null,
    string? IconGlyphKey = null);
