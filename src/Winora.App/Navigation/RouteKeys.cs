namespace Winora.App.Navigation;

/// <summary>
/// Stable route identifiers. These are never derived from a localized label, because a route key
/// that changes with the display language would invalidate navigation state on a locale switch.
/// </summary>
public static class RouteKeys
{
    public const string Dashboard = "dashboard";
    public const string Themes = "themes";
    public const string Taskbar = "taskbar";
    public const string Performance = "performance";
    public const string Cleanup = "cleanup";
    public const string Sounds = "sounds";
    public const string Cursors = "cursors";
    public const string Icons = "icons";
    public const string Startup = "startup";
    public const string Changes = "changes";
    public const string Backups = "backups";
    public const string Journal = "journal";
    public const string Settings = "settings";
    public const string Compatibility = "compatibility";
    public const string ChangeReview = "change-review";
    public const string RollbackReview = "rollback-review";
    public const string Applying = "applying";
    public const string ResultSuccess = "result-success";
    public const string ResultFailure = "result-failure";
    public const string ResultPartialRecovery = "result-partial-recovery";
    public const string Recovery = "recovery";

    public static IReadOnlyList<string> All { get; } =
    [
        Dashboard,
        Themes,
        Taskbar,
        Performance,
        Cleanup,
        Sounds,
        Cursors,
        Icons,
        Startup,
        Changes,
        Backups,
        Journal,
        Settings,
        Compatibility,
        ChangeReview,
        RollbackReview,
        Applying,
        ResultSuccess,
        ResultFailure,
        ResultPartialRecovery,
        Recovery,
    ];
}
