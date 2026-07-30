namespace Winora.App.Navigation;

/// <summary>
/// Navigation as seen by ViewModels: route keys only, never page types and never a WinUI Frame.
/// </summary>
public interface INavigationService
{
    /// <summary>The route currently displayed, or null before the first navigation.</summary>
    string? CurrentRouteKey { get; }

    event EventHandler<string>? Navigated;

    /// <summary>Navigates to a registered route. Throws for an unregistered key.</summary>
    void NavigateTo(string routeKey, object? parameter = null);

    bool CanGoBack { get; }

    void GoBack();
}
