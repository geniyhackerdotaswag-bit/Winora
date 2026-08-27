using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class BypassPage : Page
{
    private readonly PageLoad _load = new();

    /// <summary>
    /// The bypass can be started or stopped from outside Winora and outlives the app, so the state
    /// on screen is re-read rather than assumed to still be what the last click made it.
    /// </summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer = new();

    public BypassPage()
    {
        ViewModel = App.Services.GetRequiredService<BypassViewModel>();
        InitializeComponent();

        _timer.Interval = StatusInterval;
        _timer.Tick += OnTick;
    }

    public BypassViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await _load.RunAsync(ViewModel.LoadAsync);
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        base.OnNavigatedFrom(e);
    }

    private void OnTick(object? sender, object e) => ViewModel.RefreshStatus();

    private void OnStrategyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BypassStrategyViewModel strategy })
        {
            ViewModel.Select(strategy);
        }
    }

    /// <summary>
    /// Opens the release folder. The path comes from Winora's own catalog, never from user input,
    /// so this cannot be pointed at an arbitrary location.
    /// </summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(ViewModel.Folder))
            {
                // Fully qualified from the root. Inside namespace Winora.App.Views a bare
                // "System.Diagnostics" binds to Winora.System.Diagnostics, which does not exist.
                global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo
                {
                    FileName = ViewModel.Folder,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception)
        {
            // No shell, or the launch was refused. Not worth interrupting the screen over.
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => ViewModel.Start();

    private void OnStopClick(object sender, RoutedEventArgs e) => ViewModel.Stop();

    /// <summary>The verdict, which is the person's to give and nobody else's.</summary>
    private void OnWorkedClick(object sender, RoutedEventArgs e) => ViewModel.Worked();

    private void OnDidNotWorkClick(object sender, RoutedEventArgs e) => ViewModel.DidNotWork();

    private async void OnCheckClick(object sender, RoutedEventArgs e) =>
        await ViewModel.CheckAsync().ConfigureAwait(true);

    private async void OnInstallClick(object sender, RoutedEventArgs e) =>
        await ViewModel.InstallAsync().ConfigureAwait(true);
}
