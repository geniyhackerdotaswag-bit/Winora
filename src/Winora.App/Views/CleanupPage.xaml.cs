using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class CleanupPage : Page
{
    public CleanupPage()
    {
        ViewModel = App.Services.GetRequiredService<CleanupViewModel>();
        InitializeComponent();
    }

    public CleanupViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CleanupRowViewModel row })
        {
            await ViewModel.CleanAsync(row).ConfigureAwait(true);
        }
    }
}
