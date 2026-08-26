using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.Controls;
using Winora.App.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        ViewModel = App.Services.GetRequiredService<DashboardViewModel>();
        InitializeComponent();
    }

    public DashboardViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync().ConfigureAwait(true);

        // The same card as the cabinet. The dashboard was empty above the fold, and the name the
        // person just typed had nowhere to land.
        var profile = App.Services.GetRequiredService<ProfileViewModel>();
        profile.Load();
        await profile.LoadStatisticsAsync().ConfigureAwait(true);
        Card.Show(profile);
    }

    private async void OnRecoverClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RecoverAsync().ConfigureAwait(true);

    /// <summary>
    /// Fills a realized tile in and listens to it.
    /// </summary>
    /// <remarks>
    /// The repeater recycles its elements, so a tile arriving here may already be carrying the
    /// subscription and the contents of a different quick action. Removing the handler before
    /// adding it is what keeps one tile from raising the event twice and navigating twice.
    /// </remarks>
    private void OnQuickTilePrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not QuickTile tile || tile.DataContext is not QuickAction action)
        {
            return;
        }

        tile.Show(action, App.Services.GetRequiredService<ILocalizationService>());
        tile.Activated -= OnQuickTileActivated;
        tile.Activated += OnQuickTileActivated;
    }

    private void OnQuickTileClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is QuickTile tile)
        {
            tile.Activated -= OnQuickTileActivated;
        }
    }

    private void OnQuickTileActivated(object? sender, string routeKey) =>
        App.Services.GetRequiredService<INavigationService>().NavigateTo(routeKey);
}
