using CommunityToolkit.Mvvm.ComponentModel;
using Winora.App.Navigation;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>
/// Backs every screen that has no implementation yet. The interaction contract forbids a blank
/// frame, so this states which section it is and that the section is not built.
/// </summary>
public sealed partial class PlaceholderViewModel : ObservableObject
{
    private readonly RouteRegistry _routes;
    private readonly ILocalizationService _text;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusBody { get; set; } = string.Empty;

    public PlaceholderViewModel(RouteRegistry routes, ILocalizationService text)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public void Load(string routeKey)
    {
        var route = _routes.Find(routeKey);
        Title = _text.Get(route.TitleResourceKey);
        StatusTitle = _text.Get("Placeholder_InDevelopment_Title");
        StatusBody = _text.Get("Placeholder_InDevelopment_Body");
    }
}
