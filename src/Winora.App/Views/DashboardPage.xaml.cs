using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.Controls;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        ViewModel = App.Services.GetRequiredService<DashboardViewModel>();
        InitializeComponent();

        // Its own instance, parsed from the catalog. The pane draws the same mark, and a geometry
        // cannot be shared between the two; see IconGeometry for what that cost once.
        if (FluentIconCatalog.TryGetPathData("discord", out var pathData))
        {
            CommunityGlyph.Data = IconGeometry.FromPathData(pathData);
        }
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
    /// Opens the project's Discord in the default browser.
    /// </summary>
    /// <remarks>
    /// The address is a constant in the view model, never anything the app read from disk or the
    /// registry, so this cannot be redirected by something the user installed.
    /// </remarks>
    private async void OnCommunityClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher
                .LaunchUriAsync(new Uri(DashboardViewModel.CommunityUrl));
        }
        catch (Exception)
        {
            // No browser, or the launch was refused. Not worth interrupting the screen over.
        }
    }
}
