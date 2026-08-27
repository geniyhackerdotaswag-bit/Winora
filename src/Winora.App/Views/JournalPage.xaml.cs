using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class JournalPage : Page
{
    private readonly PageLoad _load = new();

    public JournalPage()
    {
        ViewModel = App.Services.GetRequiredService<JournalViewModel>();
        InitializeComponent();
    }

    public JournalViewModel ViewModel { get; }

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
}
