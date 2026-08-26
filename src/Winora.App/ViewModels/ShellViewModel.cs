using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Navigation;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>
/// Backs the navigation shell. Owns the pane structure and the selected route key, and knows nothing
/// about concrete page types — those stay in the page catalog so this stays testable.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>
    /// The project's Discord.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Moved here from the dashboard on 2026-08-26, along with the button. A link to the project is
    /// a property of the shell rather than of one page, and in the corner of a single screen it
    /// read as an element somebody forgot to remove.
    /// </para>
    /// <para>
    /// A literal rather than a setting: there is one server, it does not vary by machine, and a
    /// link nothing can rewrite cannot be pointed elsewhere by anything the app happens to read.
    /// </para>
    /// </remarks>
    public const string CommunityUrl = "https://discord.gg/bJCWdzx4D6";

    private readonly RouteRegistry _routes;
    private readonly ILocalizationService _text;
    private readonly IAppEnvironment _environment;

    /// <remarks>
    /// A partial property, not a field: MVVMTK0045 requires this form in WinUI 3 so the CsWinRT
    /// generators can emit the WinRT marshalling code.
    /// </remarks>
    [ObservableProperty]
    public partial string SelectedRouteKey { get; set; }

    /// <summary>Tooltip on the community button, which shows no text of its own.</summary>
    [ObservableProperty]
    public partial string CommunityTooltip { get; set; } = string.Empty;

    /// <summary>"Winora 0.3.8.0" at the foot of the pane, or nothing.</summary>
    /// <remarks>
    /// <para>
    /// Text, not a link. A version number is a fact, not a control; the update button lives on the
    /// settings screen, and the Settings item sits directly above this line. Something that looks
    /// like text and behaves like a link is worse than an honest label.
    /// </para>
    /// <para>
    /// Empty when the assembly carries no version. An empty line beats the word "unknown": a pane
    /// that states its own ignorance is worse than one that says nothing.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial string VersionLabel { get; set; } = string.Empty;

    public ShellViewModel(RouteRegistry routes, ILocalizationService text, IAppEnvironment environment)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
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

        CommunityTooltip = _text.Get("Shell_CommunityAction");

        var version = Displayable(_environment.Version);
        VersionLabel = version.Length == 0
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, _text.Get("Shell_Version"), version);
    }

    /// <summary>Strips the part of the version nobody reading a pane wants.</summary>
    /// <remarks>
    /// <para>
    /// AssemblyInformationalVersion carries "+&lt;commit&gt;" whenever SourceLink is on, so the raw
    /// value reads "0.4.0+dff5eee03627861…" and the pane showed forty characters of hexadecimal
    /// trimmed with an ellipsis. The commit belongs in a bug report, not in the corner of a window.
    /// </para>
    /// <para>
    /// A pre-release label after "-" is kept: "0.4.0-beta.1" is something a person needs to know
    /// they are running. The update checker's own parser drops both, because it is ordering
    /// releases rather than describing one, and it sits in the system layer, which a view model is
    /// forbidden to reach — hence the four lines here rather than a call to it.
    ///
    /// (Naming that layer in full, even inside a comment, fails
    /// SolutionStructureTests.ViewModels_never_reference_infrastructure_or_system_directly: it
    /// matches source text and cannot tell a mention from a using.)
    /// </para>
    /// </remarks>
    private static string Displayable(string version)
    {
        var trimmed = version.Trim();
        var build = trimmed.IndexOf('+', StringComparison.Ordinal);

        return build < 0 ? trimmed : trimmed[..build];
    }
}
