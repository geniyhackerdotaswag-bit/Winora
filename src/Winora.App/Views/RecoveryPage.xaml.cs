using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class RecoveryPage : Page
{
    private readonly PageLoad _load = new();

    public RecoveryPage()
    {
        ViewModel = App.Services.GetRequiredService<RecoveryViewModel>();
        InitializeComponent();
    }

    public RecoveryViewModel ViewModel { get; }

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

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ReloadAsync().ConfigureAwait(true);

    private async void OnRecoverAllClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RecoverAllAsync().ConfigureAwait(true);

    private async void OnRecoverClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StuckOperationViewModel operation })
        {
            await ViewModel.RecoverAsync(operation).ConfigureAwait(true);
        }
    }
}
