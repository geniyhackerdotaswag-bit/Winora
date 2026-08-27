using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class ChangesPage : Page
{
    private readonly PageLoad _load = new();

    public ChangesPage()
    {
        ViewModel = App.Services.GetRequiredService<ChangesViewModel>();
        InitializeComponent();
    }

    public ChangesViewModel ViewModel { get; }

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

    private async void OnRollBackClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ChangeRecordViewModel record })
        {
            await ViewModel.RollBackAsync(record).ConfigureAwait(true);
        }
    }
}
