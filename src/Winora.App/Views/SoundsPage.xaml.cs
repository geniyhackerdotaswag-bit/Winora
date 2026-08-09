using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class SoundsPage : Page
{
    public SoundsPage()
    {
        ViewModel = App.Services.GetRequiredService<SoundsViewModel>();
        InitializeComponent();
    }

    public SoundsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SoundChoiceViewModel choice })
        {
            ViewModel.Preview(choice);
        }
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SoundChoiceViewModel choice })
        {
            await ViewModel.ApplyAsync(choice).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Opens the sound folder. The path comes from Winora's own service, never from anything the
    /// user typed, so this cannot be pointed somewhere else.
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
            // The path is on screen and selectable, so a failure here costs nothing.
        }
    }
}
