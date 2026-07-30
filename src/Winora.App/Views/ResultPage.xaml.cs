using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;

namespace Winora.App.Views;

/// <summary>
/// Serves the applying, success, failure, and partial-recovery routes. The heading follows the route
/// so the user always knows which of the separate stages they are looking at.
/// </summary>
public sealed partial class ResultPage : Page
{
    private readonly ILocalizationService _text;

    public ResultPage()
    {
        ViewModel = App.Services.GetRequiredService<ChangeSessionViewModel>();
        _text = App.Services.GetRequiredService<ILocalizationService>();
        InitializeComponent();

        RollbackButton.Content = _text.Get("Action_Rollback");
        BackButton.Content = _text.Get("Action_Back");
    }

    public ChangeSessionViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string routeKey)
        {
            Heading.Text = _text.Get(HeadingKeyFor(routeKey));
        }
    }

    private static string HeadingKeyFor(string routeKey) => routeKey switch
    {
        "applying" => "Nav_Applying",
        "result-success" => "Nav_ResultSuccess",
        "result-failure" => "Nav_ResultFailure",
        "result-partial-recovery" => "Nav_ResultPartialRecovery",
        "recovery" => "Nav_Recovery",
        _ => "Nav_ResultSuccess",
    };
}
