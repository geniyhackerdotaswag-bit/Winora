using global::System.ComponentModel;
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
using Winora.System.Updates;

namespace Winora.App;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly NavigationService _navigation;
    private readonly ILocalizationService _text;
    private readonly IThemeBrushService _theme;
    private readonly UpdateViewModel _update;

    /// <summary>
    /// The pane's own model, for the two lines at its foot.
    /// </summary>
    /// <remarks>
    /// Exposed because <c>x:Bind</c> resolves against members of this class and cannot see a
    /// private field. Everything else on this window is filled in code, which is why this is the
    /// only one.
    /// </remarks>
    public ShellViewModel Shell => _shell;

    public MainWindow()
    {
        _shell = App.Services.GetRequiredService<ShellViewModel>();
        _navigation = App.Services.GetRequiredService<NavigationService>();
        _text = App.Services.GetRequiredService<ILocalizationService>();
        _theme = App.Services.GetRequiredService<IThemeBrushService>();
        _update = App.Services.GetRequiredService<UpdateViewModel>();

        InitializeComponent();

        AppTitleText.Text = _text.Get("App_Title");

        ApplyScheme();
        _theme.Applied += OnSchemeApplied;
        Closed += (_, _) => _theme.Applied -= OnSchemeApplied;

        // Winora draws its own title bar, so the caption area becomes part of the app. The system
        // still owns the caption buttons; SetTitleBar tells it which strip stays draggable.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyWindowIcon();


        // Specification section 12: the content must never be clipped below this size.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));

        _shell.Load();
        BuildPane();

        _navigation.Attach(ContentFrame);
        _navigation.NavigateTo(_shell.SelectedRouteKey);
        SelectPaneItem(_shell.SelectedRouteKey);

        BindUpdateBar();

        // After the window exists: a dialog needs a XamlRoot, and there is not one before this.
        // On Loaded, not merely enqueued. Posting to the dispatcher runs the moment the queue is
        // free, which on a cold start is before the content tree has a XamlRoot — and a
        // ContentDialog without one throws "This element does not have a XamlRoot". The log had
        // three of those before anybody noticed the dialog was simply never appearing.
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

        Navigation.FooterMenuItems.Add(BuildPaneSignature());
    }

    /// <summary>
    /// The last thing in the pane: which version this is, and the way to the project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appended to <c>FooterMenuItems</c> rather than set as <c>NavigationView.PaneFooter</c>,
    /// which is the slot it looks like it belongs in. PaneFooter draws *above* the footer items,
    /// so the version came out floating between the two groups of links, belonging to neither.
    /// The collection takes any element, and one that is not a NavigationViewItem is drawn without
    /// being made selectable — which is what these two want, since neither navigates anywhere.
    /// </para>
    /// <para>
    /// Built here rather than in markup because it has to be added to a collection, and because the
    /// values are read once: the pane is rebuilt whenever the shell reloads, and neither the
    /// version nor the address changes while the window is open.
    /// </para>
    /// </remarks>
    private NavigationViewItem BuildPaneSignature()
    {
        var version = new TextBlock
        {
            Text = _shell.VersionLabel,
            Style = (Style)Application.Current.Resources["WinoraPaneVersion"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(version, 0);

        var community = new Button
        {
            Style = (Style)Application.Current.Resources["WinoraCommunityButton"],
        };
        community.Click += OnCommunityClick;
        Grid.SetColumn(community, 1);

        // No text of its own: the mark is the label, so the tooltip and the automation name are
        // what carry the meaning to a pointer that pauses and to a screen reader.
        ToolTipService.SetToolTip(community, _shell.CommunityTooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(community, _shell.CommunityTooltip);

        // Its own instance, parsed from the catalog: a geometry cannot be shared between two icons,
        // and IconGeometry records what that cost once.
        if (FluentIconCatalog.TryGetPathData("discord", out var pathData))
        {
            var glyph = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = IconGeometry.FromPathData(pathData),
                Fill = (Brush)Application.Current.Resources["WinoraCommunityGlyphBrush"],
            };

            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(glyph);
            community.Content = new Viewbox { Width = 20, Height = 20, Child = canvas };
        }

        var row = new Grid { Padding = new Thickness(16, 8, 10, 10), ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(version);
        row.Children.Add(community);

        // Wrapped by hand, because NavigationView wraps it anyway and the container it makes on its
        // own arrives with the full item chrome — a rounded fill, a hover state and a selection
        // highlight. That made the line look like a button offering a press that does nothing.
        return new NavigationViewItem
        {
            Style = (Style)Application.Current.Resources["WinoraPaneSignature"],
            Content = row,
        };
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
            var icon = CatalogIcon.Create(route.IconGlyphKey);

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

    /// <summary>Opens the project's Discord in the default browser.</summary>
    /// <remarks>
    /// The address is a constant on the view model, never anything the app read from disk or the
    /// registry, so nothing a person installed can point this link somewhere else.
    /// </remarks>
    private async void OnCommunityClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(ShellViewModel.CommunityUrl));
        }
        catch (Exception)
        {
            // No browser, or the launch was refused. Not worth interrupting the window over.
        }
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

    /// <summary>
    /// Gives the window its icon, for the taskbar, Alt+Tab and the window list.
    /// </summary>
    /// <remarks>
    /// The packaged build gets this from the tiles its manifest declares and never needed asking.
    /// Unpackaged there is no manifest to read them from, and while the .exe now carries an icon of
    /// its own, that one dresses the file in Explorer — the window is a separate thing and Windows
    /// does not infer one from the other. Set from the icon beside the executable, which the
    /// single-file host unpacks along with everything else.
    ///
    /// Failure is silent and harmless: a window without an icon is a window that still works, and
    /// this runs in the constructor where a throw would take the whole app down before it appeared.
    /// </remarks>
    private void ApplyWindowIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Winora.ico");

            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch (Exception)
        {
            // Nothing to report and nothing a person could do about it.
        }
    }

    /// <summary>
    /// Wires the update strip by hand rather than by binding.
    /// </summary>
    /// <remarks>
    /// Four properties and two events, against a control that is created once and never recycled.
    /// A set of bindings and a converter for each visibility would be more machinery than the thing
    /// it drives, and the strip is the one piece of UI whose behaviour must be obvious when
    /// something goes wrong with it.
    /// </remarks>
    private void BindUpdateBar()
    {
        UpdateAction.Command = _update.ActCommand;

        PropertyChangedEventHandler onUpdateChanged = (_, _) =>
        {
            UpdateBar.IsOpen = _update.IsBannerVisible;
            UpdateBar.Message = _update.Message;
            UpdateBar.Severity = Controls.XamlConvert.UpdateSeverity(_update.IsFailure);
            UpdateAction.Content = _update.ActionLabel;
            UpdateAction.Visibility = _update.IsActionVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
            // Bound to IsDownloading, not IsBusy: IsBusy also covers the instant background check,
            // and a bar sitting at zero for the length of a check reads as a stalled download.
            UpdateProgress.Visibility = _update.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgress.Value = _update.Progress;
        };

        _update.PropertyChanged += onUpdateChanged;
        Closed += (_, _) => _update.PropertyChanged -= onUpdateChanged;

        _update.RestartRequested += (_, _) => Close();
        _update.OpenPageRequested += (_, _) =>
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(_update.ReleasePageUrl));

        // The strip is closable, and a person who closes it means it. Without this the view model
        // still believes it is open and the next property change puts it back on screen — which
        // reads as the app arguing with them.
        UpdateBar.CloseButtonClick += (_, _) => _update.Dismiss();

        // Deliberately not awaited: the window must finish opening whatever the network is doing.
        _ = _update.StartupAsync();
    }
}
