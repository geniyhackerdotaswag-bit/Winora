using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class AppearancePage : Page
{
    public AppearancePage()
    {
        ViewModel = App.Services.GetRequiredService<AppearanceViewModel>();
        InitializeComponent();
    }

    public AppearanceViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The preset identifier travels on the button's tag, which is the stable slug and never the
    /// display name — the same rule route keys follow, and for the same reason.
    /// </summary>
    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ViewModel.SelectPreset(id);
        }
    }

    private void OnCanvasColorChanged(ColorPicker sender, ColorChangedEventArgs args) =>
        ViewModel.SetCanvas(args.NewColor);

    private void OnAccentColorChanged(ColorPicker sender, ColorChangedEventArgs args) =>
        ViewModel.SetAccent(args.NewColor);

    private void OnOnAccentColorChanged(ColorPicker sender, ColorChangedEventArgs args) =>
        ViewModel.SetOnAccent(args.NewColor);

    private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel.Reset();

    /// <summary>
    /// Opens the Windows colour settings page.
    /// </summary>
    /// <remarks>
    /// <c>ms-settings:personalization-colors</c> is a documented launch URI:
    /// <see href="https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app" />.
    /// It is a constant here, never anything read from disk or the registry, so nothing the user
    /// installed can redirect it.
    /// </remarks>
    private async void OnOpenWindowsColorsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-colors"));
        }
        catch (Exception ex)
        {
            Diagnostics.DiagnosticSink.Write("Appearance.OpenWindowsColors", ex);
        }
    }

    /// <summary>
    /// Carries the scheme on screen across to Windows.
    /// </summary>
    /// <remarks>
    /// Only from here. Nothing on this page applies to Windows as a side effect of picking colours
    /// for Winora — the system's appearance is the person's to change, on purpose.
    /// </remarks>
    private async void OnApplyToWindowsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ApplyToWindowsAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.DiagnosticSink.Write("Appearance.ApplyToWindows", ex);
        }
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ApplyAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Writing the scheme can fail on a store that is read-only or full. The colours are
            // already painted by then, so the app is usable; recording it beats an unhandled
            // exception out of an async void handler, which would close the window.
            Diagnostics.DiagnosticSink.Write("Appearance.Apply", ex);
        }
    }
}
