using Microsoft.Extensions.DependencyInjection;
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

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: ShellPreferenceRowViewModel row } combo)
        {
            return;
        }

        // Putting the list back to reality also raises SelectionChanged; applying then would fight
        // the user, and filling the list during load raises it too.
        if (row.IsSettingProgrammatically)
        {
            return;
        }

        await ViewModel.SelectAsync(row, combo.SelectedItem as ShellPreferenceChoice).ConfigureAwait(true);
    }
}
