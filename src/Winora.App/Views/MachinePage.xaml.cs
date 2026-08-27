using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class MachinePage : Page
{
    private readonly PageLoad _load = new();

    public MachinePage()
    {
        ViewModel = App.Services.GetRequiredService<MachineViewModel>();
        InitializeComponent();
    }

    public MachineViewModel ViewModel { get; }

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
    /// Puts the whole description on the clipboard.
    /// </summary>
    /// <remarks>
    /// The clipboard is the point of the screen. Anything that goes wrong here is reported on the
    /// page rather than thrown out of an async void handler, which would take the window with it.
    /// </remarks>
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(ViewModel.AsText());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            ViewModel.ReportCopied();
        }
        catch (Exception ex)
        {
            Diagnostics.DiagnosticSink.Write("Machine.Copy", ex);
            ViewModel.ReportCopyFailed();
        }
    }
}
