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
        RouteKeys.Dashboard => typeof(DashboardPage),
        RouteKeys.Themes => typeof(ThemesPage),
        RouteKeys.Appearance => typeof(AppearancePage),
        RouteKeys.Taskbar => typeof(TaskbarPage),
        RouteKeys.Explorer => typeof(ExplorerPage),
        RouteKeys.Cleanup => typeof(CleanupPage),
        RouteKeys.Startup => typeof(StartupPage),
        RouteKeys.Performance => typeof(PerformancePage),
        RouteKeys.Cursors => typeof(CursorsPage),
        RouteKeys.Bypass => typeof(BypassPage),
        RouteKeys.Changes => typeof(ChangesPage),
        RouteKeys.Recovery => typeof(RecoveryPage),
        RouteKeys.Backups => typeof(BackupsPage),
        RouteKeys.Journal => typeof(JournalPage),
        RouteKeys.Profile => typeof(ProfilePage),
        RouteKeys.Settings => typeof(SettingsPage),
        RouteKeys.Sounds => typeof(SoundsPage),
        RouteKeys.ChangeReview => typeof(ChangeReviewPage),
        RouteKeys.Applying => typeof(ResultPage),
        RouteKeys.ResultSuccess => typeof(ResultPage),
        RouteKeys.ResultFailure => typeof(ResultPage),
        _ => typeof(PlaceholderPage),
    };
}
