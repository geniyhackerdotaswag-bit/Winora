using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class TaskbarPage : Page
{
    public TaskbarPage()
    {
        ViewModel = App.Services.GetRequiredService<TaskbarViewModel>();
        InitializeComponent();
    }

    public TaskbarViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ShellPreferenceRowViewModel row })
        {
            ViewModel.PreviewCommand.Execute(row);
        }
    }
}
