using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;
using Winora.Core.Profile;

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

    /// <summary>
    /// Opens the dialog and hands what came back to the view model.
    /// </summary>
    /// <remarks>
    /// The dialog is here rather than in the view model because it needs the window, and a view
    /// model in this project holds no window and no WinUI type of any kind. What crosses back is a
    /// path — a string — and everything that happens to it afterwards is testable without one.
    /// </remarks>
    private async Task ChooseAsync(ProfilePictureKind kind)
    {
        var path = await PicturePicker.PickAsync(App.CurrentWindow).ConfigureAwait(true);

        ViewModel.ApplyPicture(kind, path);
    }

    private async void OnChooseAvatarClick(object sender, RoutedEventArgs e) =>
        await ChooseAsync(ProfilePictureKind.Avatar).ConfigureAwait(true);

    private async void OnChooseBackgroundClick(object sender, RoutedEventArgs e) =>
        await ChooseAsync(ProfilePictureKind.CardBackground).ConfigureAwait(true);

    private void OnRemoveAvatarClick(object sender, RoutedEventArgs e) =>
        ViewModel.RemovePicture(ProfilePictureKind.Avatar);

    private void OnRemoveBackgroundClick(object sender, RoutedEventArgs e) =>
        ViewModel.RemovePicture(ProfilePictureKind.CardBackground);
}
