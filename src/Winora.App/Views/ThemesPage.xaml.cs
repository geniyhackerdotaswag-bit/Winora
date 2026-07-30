using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class ThemesPage : Page
{
    public ThemesPage()
    {
        ViewModel = App.Services.GetRequiredService<ThemesViewModel>();
        InitializeComponent();
    }

    public ThemesViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VisualEffectRowViewModel row })
        {
            ViewModel.PreviewCommand.Execute(row);
        }
    }
}
