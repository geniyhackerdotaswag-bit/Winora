using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class BackupsPage : Page
{
    public BackupsPage()
    {
        ViewModel = App.Services.GetRequiredService<BackupsViewModel>();
        InitializeComponent();
    }

    public BackupsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ReloadAsync().ConfigureAwait(true);

    private async void OnCreateClick(object sender, RoutedEventArgs e) =>
        await ViewModel.CreateAsync().ConfigureAwait(true);

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StateBackupViewModel backup })
        {
            await ViewModel.RestoreAsync(backup).ConfigureAwait(true);
        }
    }
}
