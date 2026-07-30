using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Winora.App.Diagnostics;
using Winora.App.Services;

namespace Winora.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        try
        {
            // Validate at startup rather than discovering a missing dependency when the user first
            // navigates to the screen that needs it.
            Services = new ServiceCollection().AddWinora().BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }
        catch (Exception ex)
        {
            DiagnosticSink.Write("ServiceProvider.Build", ex);
            throw;
        }

        // Without this, a failure inside page construction or an async void navigation handler is
        // swallowed and the shell silently keeps showing the previous page.
        UnhandledException += (_, args) =>
        {
            DiagnosticSink.Write("Application.UnhandledException", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                DiagnosticSink.Write("AppDomain.UnhandledException", exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticSink.Write("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// The composition root. Pages resolve their ViewModel through this because WinUI constructs
    /// pages itself and offers no constructor injection hook.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            DiagnosticSink.Write("OnLaunched", ex);
            throw;
        }
    }
}
