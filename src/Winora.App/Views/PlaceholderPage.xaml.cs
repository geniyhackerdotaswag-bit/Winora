using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage()
    {
        ViewModel = App.Services.GetRequiredService<PlaceholderViewModel>();
        InitializeComponent();
    }

    public PlaceholderViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string routeKey)
        {
            ViewModel.Load(routeKey);
        }
    }
}
