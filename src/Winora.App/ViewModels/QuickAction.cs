namespace Winora.App.ViewModels;

/// <summary>One tile on the dashboard: where it leads, and what it calls itself.</summary>
/// <remarks>
/// The name and the icon are not values of its own. They are copies of what the route registry
/// hands the pane, taken at load time, and a tile has no licence to call a section anything other
/// than what the pane calls it. Two lists of the same names agree until the day one of them
/// changes, and then they disagree in silence.
/// </remarks>
/// <param name="RouteKey">Key of the route the tile opens.</param>
/// <param name="Title">Resource key for the name — the same one the pane item uses.</param>
/// <param name="IconGlyphKey">Icon catalog key — the same one the pane item uses.</param>
/// <param name="Description">
/// Resource key for the line saying why to go there. The only thing a tile adds to what the pane
/// already shows; without it the tiles would be a copy of the pane sitting ten centimetres right.
/// </param>
public sealed record QuickAction(
    string RouteKey,
    string Title,
    string IconGlyphKey,
    string Description);
