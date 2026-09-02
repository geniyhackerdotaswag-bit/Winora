using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class LicencePage : Page
{
    private readonly PageLoad _load = new();

    public LicencePage()
    {
        ViewModel = App.Services.GetRequiredService<LicenceViewModel>();
        InitializeComponent();
    }

    public LicenceViewModel ViewModel { get; }

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

    private async void OnActivateClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ActivateAsync().ConfigureAwait(true);

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshAsync().ConfigureAwait(true);

    private void OnForgetClick(object sender, RoutedEventArgs e) => ViewModel.Forget();
}
