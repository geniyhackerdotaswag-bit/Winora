using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class PerformancePage : Page
{
    private readonly PageLoad _load = new();

    /// <summary>
    /// One second, matching what Task Manager shows by default. Faster reads as noise rather than
    /// information, and every tick costs a walk of the process list.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _timer = new();

    public PerformancePage()
    {
        ViewModel = App.Services.GetRequiredService<PerformanceViewModel>();
        InitializeComponent();

        // The timer lives here rather than in the view model: view models in this project do not
        // touch WinUI, and a background thread writing bound properties would break the binding.
        _timer.Interval = RefreshInterval;
        _timer.Tick += OnTick;
    }

    public PerformanceViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await _load.RunAsync(ViewModel.LoadAsync);

        // Not started while the screen is closed: the timer's only job is to refresh figures that
        // are not on screen.
        if (!ViewModel.IsUnderMaintenance)
        {
            _timer.Start();
        }
    }

    /// <summary>
    /// Stopped on the way out. A timer left running would keep walking the whole process list for
    /// a page nobody is looking at.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        base.OnNavigatedFrom(e);
    }

    private void OnTick(object? sender, object e) => ViewModel.Refresh();

    private void OnPanelClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PerformancePanelViewModel panel })
        {
            ViewModel.Select(panel);
        }
    }

}
