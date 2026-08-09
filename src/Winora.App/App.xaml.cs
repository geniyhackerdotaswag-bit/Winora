using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Winora.App.Diagnostics;
using Winora.App.Services;
using Winora.Core.Appearance;

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

            // Rethrowing alone ends the process before a window ever exists, so the shortcut simply
            // does nothing and the user has no way to know a log was even written. Say so first.
            StartupFailureNotice.Show(DiagnosticSink.LogPath);
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
            // Winora runs elevated by the owner's decision. When it was started without rights it
            // hands off to an elevated copy of itself and leaves without ever showing a window, so
            // the user sees one app, not two. A declined prompt falls through and runs as-is.
            if (Services.GetRequiredService<IElevationRelauncher>().TryRelaunchElevated())
            {
                Exit();
                return;
            }

            ApplyStoredScheme();

            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            DiagnosticSink.Write("OnLaunched", ex);
            throw;
        }
    }

    /// <summary>
    /// Paints the user's colours before the first window exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before, rather than after, so nothing is ever shown in the default scheme and then repainted
    /// — the flash would be the first thing a user with a light scheme saw on every launch.
    /// </para>
    /// <para>
    /// Blocking on the read is deliberate. It is one small file and there is no window to keep
    /// responsive yet, whereas an <c>async void</c> launch path would let the window be constructed
    /// against whichever colours happened to be loaded by then. The store is documented never to
    /// throw for a missing or damaged file; the catch is here for the case it is wrong about that,
    /// because a colour preference must never be the reason the app fails to start.
    /// </para>
    /// </remarks>
    private static void ApplyStoredScheme()
    {
        try
        {
            var load = Services.GetRequiredService<IColorSchemeStore>()
                .LoadAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();

            Services.GetRequiredService<IThemeBrushService>().Apply(load.Scheme);
        }
        catch (Exception ex)
        {
            DiagnosticSink.Write("ApplyStoredScheme", ex);
        }
    }
}
