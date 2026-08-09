using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnAppearanceClick(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<Navigation.INavigationService>()
            .NavigateTo(Navigation.RouteKeys.Appearance);

    /// <summary>
    /// Opens the store folder. The path comes from Winora's own configuration, never from user
    /// input, so this cannot be pointed at an arbitrary location.
    /// </summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ViewModel.StoragePath);
            Process.Start(new ProcessStartInfo
            {
                FileName = ViewModel.StoragePath,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // The path is shown on screen and selectable, so a failure here costs nothing.
        }
    }
}
