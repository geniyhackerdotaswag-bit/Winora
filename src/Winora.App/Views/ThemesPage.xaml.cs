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

    private async void OnToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: VisualEffectRowViewModel row } toggle)
        {
            return;
        }

        // Moving the switch back to reality also raises Toggled; applying then would fight the user.
        if (row.IsSettingProgrammatically)
        {
            return;
        }

        await ViewModel.ToggleAsync(row, toggle.IsOn).ConfigureAwait(true);
    }
}
