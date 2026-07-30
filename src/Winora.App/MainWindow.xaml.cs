using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Winora.App.Controls;
using Winora.App.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;

namespace Winora.App;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly NavigationService _navigation;
    private readonly ILocalizationService _text;

    public MainWindow()
    {
        _shell = App.Services.GetRequiredService<ShellViewModel>();
        _navigation = App.Services.GetRequiredService<NavigationService>();
        _text = App.Services.GetRequiredService<ILocalizationService>();

        InitializeComponent();

        // Specification section 12: the content must never be clipped below this size.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));

        _shell.Load();
        BuildPane();

        _navigation.Attach(ContentFrame);
        _navigation.NavigateTo(_shell.SelectedRouteKey);
        SelectPaneItem(_shell.SelectedRouteKey);
    }

    private void BuildPane()
    {
        Navigation.MenuItems.Clear();
        Navigation.FooterMenuItems.Clear();

        foreach (var route in _shell.RootItems)
        {
            Navigation.MenuItems.Add(CreateItem(route));
        }

        foreach (var group in _shell.Groups)
        {
            Navigation.MenuItems.Add(new NavigationViewItemSeparator());
            Navigation.MenuItems.Add(new NavigationViewItemHeader { Content = _text.Get(group.Key) });
            foreach (var route in group)
            {
                Navigation.MenuItems.Add(CreateItem(route));
            }
        }

        foreach (var route in _shell.FooterItems)
        {
            Navigation.FooterMenuItems.Add(CreateItem(route));
        }
    }

    private NavigationViewItem CreateItem(RouteDescriptor route)
    {
        var label = _text.Get(route.TitleResourceKey);
        var item = new NavigationViewItem
        {
            Content = label,
            Tag = route.Key,
        };

        if (route.IconGlyphKey is not null && FluentIconCatalog.TryGetGlyph(route.IconGlyphKey, out var glyph))
        {
            item.Icon = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 20,
            };
        }

        // Every icon-bearing control carries an automation name; an icon alone is not a label.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, label);
        return item;
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
        if (args.SelectedItem is NavigationViewItem { Tag: string routeKey })
        {
            _shell.SelectedRouteKey = routeKey;
            _navigation.NavigateTo(routeKey);
        }
    }
}
