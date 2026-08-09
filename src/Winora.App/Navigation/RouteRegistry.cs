namespace Winora.App.Navigation;

/// <summary>
/// The single source of truth for which screens exist. Lookup of an unregistered key throws rather
/// than returning a default, so a typo surfaces as a test or crash instead of a click that appears
/// to do nothing.
/// </summary>
public sealed class RouteRegistry
{
    private const string GroupPersonalization = "Nav_Group_Personalization";
    private const string GroupMaintenance = "Nav_Group_Maintenance";
    private const string GroupSystem = "Nav_Group_System";

    private readonly Dictionary<string, RouteDescriptor> _byKey;

    private RouteRegistry(IReadOnlyList<RouteDescriptor> routes)
    {
        Routes = routes;
        _byKey = routes.ToDictionary(static route => route.Key, StringComparer.Ordinal);
    }

    public IReadOnlyList<RouteDescriptor> Routes { get; }

    public string StartRouteKey => RouteKeys.Dashboard;

    public static RouteRegistry Create() => new(
    [
        new(RouteKeys.Dashboard, "Nav_Dashboard", RoutePlacement.PaneRoot, IconGlyphKey: "home"),

        new(RouteKeys.Themes, "Nav_Themes", RoutePlacement.Pane, GroupPersonalization, "color"),

        new(RouteKeys.Taskbar, "Nav_Taskbar", RoutePlacement.Pane, GroupPersonalization, "taskbar"),
        new(RouteKeys.Sounds, "Nav_Sounds", RoutePlacement.Pane, GroupPersonalization, "sound"),
        new(RouteKeys.Cursors, "Nav_Cursors", RoutePlacement.Pane, GroupPersonalization, "cursor"),

        new(RouteKeys.Performance, "Nav_Performance", RoutePlacement.Pane, GroupMaintenance, "speed"),
        new(RouteKeys.Cleanup, "Nav_Cleanup", RoutePlacement.Pane, GroupMaintenance, "broom"),

        new(RouteKeys.Startup, "Nav_Startup", RoutePlacement.Pane, GroupSystem, "startup"),
        new(RouteKeys.Bypass, "Nav_Bypass", RoutePlacement.Pane, GroupSystem, "discord"),
        new(RouteKeys.Changes, "Nav_Changes", RoutePlacement.Pane, GroupSystem, "history"),
        new(RouteKeys.Backups, "Nav_Backups", RoutePlacement.Pane, GroupSystem, "backup"),

        new(RouteKeys.Journal, "Nav_Journal", RoutePlacement.Footer, IconGlyphKey: "journal"),
        new(RouteKeys.Settings, "Nav_Settings", RoutePlacement.Footer, IconGlyphKey: "settings"),

        // Reached from the settings screen, not from the pane. Winora's own colours are a preference
        // about the app; the personalization group is for screens that change Windows, and an item
        // there implied this one did too.
        new(RouteKeys.Appearance, "Nav_Appearance", RoutePlacement.RouteOnly, IconGlyphKey: "appearance"),

        new(RouteKeys.ChangeReview, "Nav_ChangeReview", RoutePlacement.RouteOnly),
        new(RouteKeys.Applying, "Nav_Applying", RoutePlacement.RouteOnly),
        new(RouteKeys.ResultSuccess, "Nav_ResultSuccess", RoutePlacement.RouteOnly),
        new(RouteKeys.ResultFailure, "Nav_ResultFailure", RoutePlacement.RouteOnly),
        new(RouteKeys.Recovery, "Nav_Recovery", RoutePlacement.RouteOnly),
    ]);

    public bool TryFind(string key, out RouteDescriptor? descriptor)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            descriptor = null;
            return false;
        }

        return _byKey.TryGetValue(key, out descriptor);
    }

    public RouteDescriptor Find(string key) =>
        TryFind(key, out var descriptor) && descriptor is not null
            ? descriptor
            : throw new KeyNotFoundException($"Route '{key}' is not registered.");
}
