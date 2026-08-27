using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly PageLoad _load = new();

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();

        // The same singleton the main window's strip is bound to, not a second instance: a manual
        // check here and the strip at the top of the window are one conversation, not two.
        Update = App.Services.GetRequiredService<UpdateViewModel>();

        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Exposed directly rather than through <see cref="SettingsViewModel" />: that would make one
    /// view model a façade for another, which nothing else in this codebase does.
    /// </summary>
    public UpdateViewModel Update { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await _load.RunAsync(ViewModel.LoadAsync);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _load.Leave();
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
