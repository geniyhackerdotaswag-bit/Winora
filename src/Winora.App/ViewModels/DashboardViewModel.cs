using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Navigation;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>
/// The first screen.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately close to empty, and it has now been emptied twice. On 2026-08-04 it lost capability
/// counters, a Windows build readout, deployment status, presets and an about card. On 2026-08-09 it
/// lost a list of recent changes with an undo beside each one, built the day before at the owner's
/// request and removed on sight: it read as clutter on the screen that is supposed to be the calm
/// one. That answer is on the Changes screen, which exists for it.
/// </para>
/// <para>
/// One thing survived both passes, and it is not the app talking about itself: the recovery
/// warning appears only when an unfinished change is blocking every other screen, and without it a
/// person meets that state as a click that refuses for no stated reason. The community button
/// survived them too and has since moved to the pane, where a link to the project belongs — it is a
/// property of the shell, not of this page, and in one screen's corner it read as an element
/// somebody forgot to remove.
/// </para>
/// <para>
/// Before adding anything here again: the test this screen keeps failing is whether the thing is
/// something a person arrived wanting, or something the app wanted to say. Three separate attempts
/// have now been removed for landing on the wrong side of it.
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject
{
    /// <summary>What the dashboard offers, left to right.</summary>
    /// <remarks>
    /// Route keys, and nothing else. The name and the icon are read from the registry at load time
    /// so that a section renamed once is renamed in both places — see <see cref="QuickAction"/>.
    /// </remarks>
    private static readonly string[] QuickActionRoutes =
    [
        RouteKeys.Themes,
        RouteKeys.Cursors,
        RouteKeys.Taskbar,
        RouteKeys.Bypass,
    ];

    private readonly IRecoveryState _recovery;
    private readonly ILocalizationService _text;
    private readonly RouteRegistry _routes;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>
    /// Shown when an earlier change never finished. Nothing can be applied until it is reconciled,
    /// so this is stated on the first screen rather than discovered on a failed click.
    /// </summary>
    [ObservableProperty]
    public partial string RecoveryNotice { get; set; } = string.Empty;

    public bool HasRecovery => !string.IsNullOrEmpty(RecoveryNotice);

    partial void OnRecoveryNoticeChanged(string value) => OnPropertyChanged(nameof(HasRecovery));

    [ObservableProperty]
    public partial string RecoveryActionLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRecovering { get; set; }

    /// <summary>Four tiles: the things the program is opened in order to do.</summary>
    /// <remarks>
    /// <para>
    /// Read the remark on this class in full before putting a fifth here, or anything beside them.
    /// The check this screen has failed three times is whether a thing is what a person arrived
    /// wanting or what the app wanted to say. Tiles are navigation to the reason the program was
    /// opened, and they pass it. Counters, a system summary and a list of recent changes did not,
    /// and the list was removed the day after it was asked for.
    /// </para>
    /// <para>
    /// Holds resource keys, not sentences. Resolving them needs the localizer, which the page has
    /// and a tile template does not.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial IReadOnlyList<QuickAction> QuickActions { get; set; } = [];

    public DashboardViewModel(IRecoveryState recovery, ILocalizationService text, RouteRegistry routes)
    {
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
    }

    /// <summary>
    /// Rolls the unfinished operation back. This is the only transition out of that state, and
    /// until it runs nothing else in the app can be applied at all.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecovering)
        {
            return;
        }

        IsRecovering = true;
        try
        {
            var outcome = await _recovery.RecoverAsync(cancellationToken).ConfigureAwait(true);

            if (outcome.Failed > 0)
            {
                RecoveryNotice = string.Format(
                    CultureInfo.CurrentCulture,
                    _text.Get("Dashboard_RecoveryFailed"),
                    outcome.Failed,
                    outcome.FirstFailure);
                return;
            }

            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsRecovering = false;
        }
    }

    /// <summary>
    /// "themes" becomes "Dashboard_Quick_Themes".
    /// </summary>
    /// <remarks>
    /// Derived rather than listed beside the route keys, so a tile cannot be added with its
    /// description quietly left off. Route keys are lowercase Latin by rule, so the invariant
    /// casing here cannot meet the Turkish dotless i.
    /// </remarks>
    private static string DescriptionKeyFor(string routeKey) =>
        $"Dashboard_Quick_{char.ToUpperInvariant(routeKey[0])}{routeKey[1..]}";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Title = _text.Get("Nav_Dashboard");
        RecoveryActionLabel = _text.Get("Dashboard_RecoveryAction");
        QuickActions = QuickActionRoutes
            .Select(_routes.Find)
            .Select(static route => new QuickAction(
                route.Key,
                route.TitleResourceKey,
                route.IconGlyphKey!,
                DescriptionKeyFor(route.Key)))
            .ToArray();

        var pending = await _recovery.PendingCountAsync(cancellationToken).ConfigureAwait(true);
        RecoveryNotice = pending > 0
            ? string.Format(CultureInfo.CurrentCulture, _text.Get("Dashboard_RecoveryPending"), pending)
            : string.Empty;
    }
}
