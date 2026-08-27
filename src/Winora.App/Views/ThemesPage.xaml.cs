using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class ThemesPage : Page
{
    private readonly PageLoad _load = new();

    public ThemesPage()
    {
        ViewModel = App.Services.GetRequiredService<ThemesViewModel>();
        InitializeComponent();
    }

    public ThemesViewModel ViewModel { get; }

    /// <summary>One user-facing string, by key, for the one label the markup needs.</summary>
    public string Localized(string resourceKey) =>
        App.Services.GetRequiredService<ILocalizationService>().Get(resourceKey);

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await _load.RunAsync(ViewModel.LoadAsync);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _load.Leave();
    }

    /// <summary>
    /// Starts this program again with administrator rights and closes this copy.
    /// </summary>
    /// <remarks>
    /// The relauncher decides whether it can: it refuses for a packaged build and for an account
    /// with no rights to gain. A refusal leaves the window exactly as it was, which is the honest
    /// outcome — nothing was promised beyond the attempt.
    /// </remarks>
    private void OnRestartElevatedClick(object sender, RoutedEventArgs e)
    {
        if (App.Services.GetRequiredService<IElevationRelauncher>().TryRelaunchElevated())
        {
            Application.Current.Exit();
        }
    }

    private async void OnTurnOnEffectsClick(object sender, RoutedEventArgs e) =>
        await ViewModel.TurnOnEffectsAsync().ConfigureAwait(true);

    private async void OnToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: VisualEffectRowViewModel row } toggle)
        {
            return;
        }

        // Moving the switch back to reality also raises Toggled; applying then would fight the user.
        if (row.IsSettingProgrammatically)
        {
            return;
        }

        await ViewModel.ToggleAsync(row, toggle.IsOn).ConfigureAwait(true);
    }
}
