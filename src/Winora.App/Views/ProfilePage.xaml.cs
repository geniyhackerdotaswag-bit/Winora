using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class ProfilePage : Page
{
    public ProfilePage()
    {
        ViewModel = App.Services.GetRequiredService<ProfileViewModel>();
        InitializeComponent();
    }

    public ProfileViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ViewModel.Load();
        await ViewModel.LoadStatisticsAsync().ConfigureAwait(true);
        Card.Show(ViewModel);

        // The card follows what the person types, so saving is not the first time they see it.
        ViewModel.PropertyChanged += (_, _) => Card.Show(ViewModel);
    }
}
