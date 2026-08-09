using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class CursorsPage : Page
{
    public CursorsPage()
    {
        ViewModel = App.Services.GetRequiredService<CursorsViewModel>();
        InitializeComponent();
    }

    public CursorsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CursorPackViewModel pack })
        {
            await ViewModel.ApplyAsync(pack).ConfigureAwait(true);
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RestoreAsync().ConfigureAwait(true);

    /// <summary>
    /// Opens the drop folder in Explorer. The path comes from Winora's own service, never from
    /// anything the user typed, so this cannot be pointed at an arbitrary location.
    /// </summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ViewModel.PackFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // The path is shown on screen and selectable, so a failure here costs nothing.
        }
    }
}
