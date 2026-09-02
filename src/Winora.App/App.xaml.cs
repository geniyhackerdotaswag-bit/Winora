using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Winora.App.Diagnostics;
using Winora.App.Services;
using Winora.Core.Appearance;

namespace Winora.App;

public partial class App : Application
{
    /// <summary>
    /// The window on screen, whichever of the two it is.
    /// </summary>
    /// <remarks>
    /// Held here because a page has no route to the window it is drawn in, and a file dialog has to
    /// be given one — a WinUI desktop app has no ambient answer to "which window owns this", and a
    /// picker created without one throws when it is opened rather than when it is built. It
    /// replaces the private field the two windows were kept in; there was never more than one of
    /// them at a time, and nothing outside this class could see it.
    /// </remarks>
    public static Window? CurrentWindow { get; private set; }

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
            // No relaunch here any more. The manifest asks for administrator before the process
            // starts, so by the time this runs the rights are already held — see app.manifest for
            // why that was chosen over starting plain and elevating afterwards.

            ApplyStoredScheme();

            // Registration comes first, and the shell is not created at all until it is done — not
            // created and hidden. A hidden window still shows in the taskbar and in Alt+Tab, which is
            // exactly the "app is visible before registration" the owner asked to remove.
            if (Services.GetRequiredService<IProfileService>().Current is null)
            {
                var registration = new Views.RegistrationWindow();

                void OnRegistrationCompleted(object? sender, EventArgs e)
                {
                    // Unsubscribed first, before anything below can throw. RegistrationViewModel.Open
                    // already refuses to raise Completed a second time itself; this is the belt to
                    // that braces, so nothing reaches this handler twice from any cause.
                    registration.Completed -= OnRegistrationCompleted;

                    try
                    {
                        // MainWindow first, registration second: WinUI posts a quit message and ends
                        // the process the moment the last window closes (Application.Start's message
                        // loop returns when the window count reaches zero). Closing registration
                        // before the shell exists would end the app instead of handing off to it.
                        CurrentWindow = new MainWindow();
                        CurrentWindow.Activate();
                        registration.Close();
                    }
                    catch (Exception ex)
                    {
                        // This runs off a button click, not inside OnLaunched's own try/catch, so a
                        // throw here reaches only the UnhandledException handler below, which logs it
                        // and marks it handled — leaving the registration window open with no
                        // explanation at all, the same silent failure StartupFailureNotice exists to
                        // prevent. The profile is already saved at this point, so a relaunch reaches
                        // the shell even when this particular handover failed.
                        DiagnosticSink.Write("Registration.Completed", ex);
                        StartupFailureNotice.Show(DiagnosticSink.LogPath);
                    }
                }

                registration.Completed += OnRegistrationCompleted;

                CurrentWindow = registration;
                registration.Activate();
                return;
            }

            CurrentWindow = new MainWindow();
            CurrentWindow.Activate();
        }
        catch (Exception ex)
        {
            DiagnosticSink.Write("OnLaunched", ex);

            // Rethrowing alone ends the process before any window exists on a first run — the exact
            // silent failure StartupFailureNotice was written for on 2026-08-04. This try covers both
            // the registration path and the returning-user path, so both get the same notice.
            StartupFailureNotice.Show(DiagnosticSink.LogPath);
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
