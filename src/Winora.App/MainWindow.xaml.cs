using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Winora.App.Controls;
using Winora.App.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;
using Winora.Core.Appearance;

namespace Winora.App;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly NavigationService _navigation;
    private readonly ILocalizationService _text;
    private readonly IThemeBrushService _theme;

    public MainWindow()
    {
        _shell = App.Services.GetRequiredService<ShellViewModel>();
        _navigation = App.Services.GetRequiredService<NavigationService>();
        _text = App.Services.GetRequiredService<ILocalizationService>();
        _theme = App.Services.GetRequiredService<IThemeBrushService>();

        InitializeComponent();

        AppTitleText.Text = _text.Get("App_Title");

        ApplyScheme();
        _theme.Applied += OnSchemeApplied;
        Closed += (_, _) => _theme.Applied -= OnSchemeApplied;

        // Winora draws its own title bar, so the caption area becomes part of the app. The system
        // still owns the caption buttons; SetTitleBar tells it which strip stays draggable.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Specification section 12: the content must never be clipped below this size.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));

        _shell.Load();
        BuildPane();

        _navigation.Attach(ContentFrame);
        _navigation.NavigateTo(_shell.SelectedRouteKey);
        SelectPaneItem(_shell.SelectedRouteKey);
    }

    private void OnSchemeApplied(object? sender, EventArgs e) => ApplyScheme();

    /// <summary>
    /// The two parts of a colour scheme that repainting the brushes cannot reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The element theme is the important one. Stock WinUI controls draw their text from the
    /// platform's own brushes, which follow the <em>system</em> light or dark setting — so a user
    /// choosing a light canvas while Windows is in dark mode would get light text on it and a
    /// screen they cannot read. Requesting the theme that matches the canvas points every one of
    /// those brushes at the right set, and does it for controls Winora has never heard of.
    /// </para>
    /// <para>
    /// The caption buttons belong to the window rather than to the visual tree, so no resource
    /// applies to them at all. Left alone, the minimise and close glyphs keep the system theme's
    /// colour and disappear against a canvas of the opposite one.
    /// </para>
    /// <para>
    /// While High Contrast is in force both are left exactly as Windows set them.
    /// </para>
    /// </remarks>
    private void ApplyScheme()
    {
        if (_theme.IsSuppressed)
        {
            RootGrid.RequestedTheme = ElementTheme.Default;
            return;
        }

        var palette = _theme.Current;
        RootGrid.RequestedTheme = palette.IsDark ? ElementTheme.Dark : ElementTheme.Light;

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = ToColor(palette.TextPrimary);
        titleBar.ButtonInactiveForegroundColor = ToColor(palette.TextFaint);
        titleBar.ButtonHoverBackgroundColor = ToColor(palette.Hover);
        titleBar.ButtonHoverForegroundColor = ToColor(palette.TextPrimary);
        titleBar.ButtonPressedBackgroundColor = ToColor(palette.Divider);
        titleBar.ButtonPressedForegroundColor = ToColor(palette.TextPrimary);
    }

    private static Windows.UI.Color ToColor(ColorValue colour) =>
        Windows.UI.Color.FromArgb(0xFF, colour.R, colour.G, colour.B);

    private void BuildPane()
    {
        Navigation.MenuItems.Clear();
        Navigation.FooterMenuItems.Clear();

        foreach (var route in _shell.RootItems)
        {
            Navigation.MenuItems.Add(CreateItem(route, hueResourceKey: null));
        }

        foreach (var group in _shell.Groups)
        {
            // No separator line: the heading and the hue of the icons below it already group them,
            // and the extra rule was what made the pane read as the Settings pane.
            //
            // The margin lines the heading up with the icon column below it. The stock header sits
            // hard against the pane edge, which left the group titles floating outside the list.
            Navigation.MenuItems.Add(new NavigationViewItemHeader
            {
                Content = _text.Get(group.Key),
                Margin = new Thickness(16, 10, 0, 2),
            });
            foreach (var route in group)
            {
                Navigation.MenuItems.Add(CreateItem(route, HueFor(group.Key)));
            }
        }

        foreach (var route in _shell.FooterItems)
        {
            Navigation.FooterMenuItems.Add(CreateItem(route, hueResourceKey: null));
        }
    }

    /// <summary>
    /// Pane icons are not tinted. Colouring them per group put three saturated hues in the pane at
    /// once and made the app look decorated; the accent is now spent only on selection.
    /// </summary>
    private static string? HueFor(string groupResourceKey) => null;

    private NavigationViewItem CreateItem(RouteDescriptor route, string? hueResourceKey)
    {
        var label = _text.Get(route.TitleResourceKey);
        var item = new NavigationViewItem
        {
            Content = label,
            Tag = route.Key,
        };

        if (route.IconGlyphKey is not null)
        {
            var icon = IconFor(route.IconGlyphKey);

            if (icon is not null &&
                hueResourceKey is not null &&
                Application.Current.Resources.TryGetValue(hueResourceKey, out var hue) &&
                hue is Brush brush)
            {
                icon.Foreground = brush;
            }

            item.Icon = icon;
        }

        // Every icon-bearing control carries an automation name; an icon alone is not a label.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, label);
        return item;
    }

    /// <summary>
    /// Resolves one catalog key to an icon, of whichever kind the catalog holds it as.
    /// </summary>
    /// <remarks>
    /// Returns null for a key the catalog does not know, and that is not a silent shrug: a route
    /// naming an unknown key is caught by <c>IconCatalogTests</c> before it can ship. It shipped
    /// once — the bypass route asked for "shield", which was never in the catalog, so the item sat
    /// in the pane with a blank space where every neighbour had an icon and nothing failed.
    /// </remarks>
    private static IconElement? IconFor(string iconGlyphKey)
    {
        if (FluentIconCatalog.TryGetGlyph(iconGlyphKey, out var glyph))
        {
            return new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 20,
            };
        }

        if (FluentIconCatalog.TryGetPathData(iconGlyphKey, out var pathData))
        {
            // Not tolerated as a null return. The catalog claims to hold this icon, so a failed
            // parse is a defect in the catalog, and swallowing it reproduces the exact bug this
            // method's remarks describe: a blank space in the pane and nothing in any log.
            var geometry = IconGeometry.FromPathData(pathData)
                ?? throw new InvalidOperationException(
                    $"Icon '{iconGlyphKey}' has path data the XAML parser rejected.");

            // A PathIcon scales its data to the icon box, so the mark lands at the same optical
            // size as the font glyphs beside it without a hand-tuned transform.
            return new PathIcon { Data = geometry };
        }

        return null;
    }

    private void SelectPaneItem(string routeKey)
    {
        foreach (var candidate in Navigation.MenuItems.Concat(Navigation.FooterMenuItems))
        {
            if (candidate is NavigationViewItem { Tag: string tag } item &&
                string.Equals(tag, routeKey, StringComparison.Ordinal))
            {
                Navigation.SelectedItem = item;
                return;
            }
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string routeKey })
        {
            return;
        }

        try
        {
            _shell.SelectedRouteKey = routeKey;
            _navigation.NavigateTo(routeKey);
        }
        catch (Exception ex)
        {
            // A throw here would be marshalled back to whatever raised the selection and leave the
            // pane pointing at a page that never loaded. Record it and say so on screen instead.
            Diagnostics.DiagnosticSink.Write($"Navigate:{routeKey}", ex);
            throw;
        }
    }
}
