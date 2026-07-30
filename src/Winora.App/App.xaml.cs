using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Winora.App.Services;

namespace Winora.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = new ServiceCollection().AddWinora().BuildServiceProvider();
    }

    /// <summary>
    /// The composition root. Pages resolve their ViewModel through this because WinUI constructs
    /// pages itself and offers no constructor injection hook.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
