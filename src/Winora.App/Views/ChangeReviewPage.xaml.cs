using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Winora.App.Services;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class ChangeReviewPage : Page
{
    public ChangeReviewPage()
    {
        ViewModel = App.Services.GetRequiredService<ChangeSessionViewModel>();
        InitializeComponent();

        var text = App.Services.GetRequiredService<ILocalizationService>();
        Heading.Text = text.Get("Nav_ChangeReview");
        ConfirmButton.Content = text.Get("Action_Confirm");
        CancelButton.Content = text.Get("Action_Cancel");
    }

    public ChangeSessionViewModel ViewModel { get; }
}
