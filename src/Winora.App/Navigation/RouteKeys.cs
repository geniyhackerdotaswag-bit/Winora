namespace Winora.App.Navigation;

/// <summary>
/// Stable route identifiers. These are never derived from a localized label, because a route key
/// that changes with the display language would invalidate navigation state on a locale switch.
/// </summary>
public static class RouteKeys
{
    public const string Dashboard = "dashboard";
    public const string Themes = "themes";
    public const string Appearance = "appearance";
    public const string Taskbar = "taskbar";

    public const string Explorer = "explorer";
    public const string Performance = "performance";
    public const string Cleanup = "cleanup";
    public const string Sounds = "sounds";
    public const string Cursors = "cursors";
    public const string Startup = "startup";
    public const string Bypass = "bypass";
    public const string Changes = "changes";
    public const string Backups = "backups";
    public const string Journal = "journal";
    public const string Settings = "settings";
    public const string Profile = "profile";
    public const string ChangeReview = "change-review";
    public const string Applying = "applying";
    public const string ResultSuccess = "result-success";
    public const string ResultFailure = "result-failure";
    public const string Recovery = "recovery";

    public static IReadOnlyList<string> All { get; } =
    [
        Dashboard,
        Themes,
        Appearance,
        Taskbar,
        Explorer,
        Performance,
        Cleanup,
        Sounds,
        Cursors,
        Startup,
        Bypass,
        Changes,
        Backups,
        Journal,
        Settings,
        Profile,
        ChangeReview,
        Applying,
        ResultSuccess,
        ResultFailure,
        Recovery,
    ];
}
